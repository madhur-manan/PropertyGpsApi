using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PropertyGpsApi.Tests;

/// <summary>
/// No private key may ever be committed to this repository. Scans every file git would commit -
/// the tracked files plus the untracked ones .gitignore does not exclude, which is exactly what
/// "git add -A" would pick up - for the start of an RSA private key in each of its usual forms.
/// The only exception is the public TEST-ONLY key inside the cross-language vector fixture,
/// under tests/.
///
/// Ignored files (appsettings.Local.json, *.private.b64, bin, obj) are deliberately not read:
/// they hold real keys on a developer machine, which is where they belong.
///
/// A failure names the file and the kind of key, never anything the match contains.
/// </summary>
public class RepositoryKeyScanTests
{
    /// <summary>SEQUENCE, INTEGER 0, rsaEncryption, NULL, OCTET STRING: PKCS#8 RSA, in base64.</summary>
    private static readonly Regex Pkcs8Rsa = new(@"MII[A-Za-z0-9+/]{3}IBADANBgkqhkiG9w0BAQEFAAS", RegexOptions.CultureInvariant);

    /// <summary>SEQUENCE, INTEGER 0, INTEGER (the modulus): PKCS#1 RSAPrivateKey, in base64.</summary>
    private static readonly Regex Pkcs1Rsa = new(@"MII[A-Za-z0-9+/]{2,3}IBAAKC", RegexOptions.CultureInvariant);

    /// <summary>Any PEM private-key block: PRIVATE KEY, RSA PRIVATE KEY, ENCRYPTED PRIVATE KEY, ...</summary>
    private static readonly Regex PemPrivateKey = new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant);

    /// <summary>How much of a match is compared with the TEST key: well past the fixed DER header.</summary>
    private const int CompareChars = 76;

    [Fact]
    public void The_patterns_catch_real_private_keys_in_every_form_and_no_public_key()
    {
        // Keys generated for this run, so no key-shaped text has to sit in this file.
        foreach (var pkcs8 in new[] { TestKeys.Primary, TestKeys.Short })
        {
            using var rsa = TestKeys.Import(pkcs8);

            Assert.Matches(Pkcs8Rsa, Convert.ToBase64String(pkcs8));
            Assert.Matches(Pkcs8Rsa, rsa.ExportPkcs8PrivateKeyPem());
            Assert.Matches(PemPrivateKey, rsa.ExportPkcs8PrivateKeyPem());

            Assert.Matches(Pkcs1Rsa, Convert.ToBase64String(rsa.ExportRSAPrivateKey()));
            Assert.Matches(Pkcs1Rsa, rsa.ExportRSAPrivateKeyPem());
            Assert.Matches(PemPrivateKey, rsa.ExportRSAPrivateKeyPem());
            Assert.DoesNotMatch(Pkcs1Rsa, Convert.ToBase64String(pkcs8));

            Assert.Matches(PemPrivateKey, rsa.ExportEncryptedPkcs8PrivateKeyPem("pw",
                new System.Security.Cryptography.PbeParameters(System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
                    System.Security.Cryptography.HashAlgorithmName.SHA256, 1000)));

            foreach (var publicKey in new[] { Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), rsa.ExportSubjectPublicKeyInfoPem(),
                         Convert.ToBase64String(rsa.ExportRSAPublicKey()), rsa.ExportRSAPublicKeyPem() })
            {
                Assert.DoesNotMatch(Pkcs8Rsa, publicKey);
                Assert.DoesNotMatch(Pkcs1Rsa, publicKey);
                Assert.DoesNotMatch(PemPrivateKey, publicKey);
            }
        }
    }

    [Fact]
    public void No_file_git_would_commit_holds_a_private_key_other_than_the_TEST_key()
    {
        var root = RepositoryRoot();
        var testKeyStart = TestKeyStart();
        var problems = new List<string>();
        var testKeySightings = 0;

        foreach (var relative in GitFiles.WouldCommit(root))
        {
            var path = Path.Combine(root, relative);
            string text;
            try
            {
                if (!File.Exists(path)) continue;   // tracked, but deleted in the working tree
                // Latin-1 maps every byte to one character, so binary files cannot throw here.
                text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;   // held open with no sharing: git cannot add it in that state either
            }

            foreach (Match match in Pkcs8Rsa.Matches(text))
            {
                var isTestKey = relative.StartsWith("tests/", StringComparison.Ordinal)
                                && match.Index + CompareChars <= text.Length
                                && string.CompareOrdinal(text, match.Index, testKeyStart, 0, CompareChars) == 0;
                if (isTestKey) testKeySightings++;
                else problems.Add($"{relative}: a PKCS#8 RSA private key that is not the TEST key");
            }

            if (Pkcs1Rsa.IsMatch(text)) problems.Add($"{relative}: a PKCS#1 RSA private key");
            if (PemPrivateKey.IsMatch(text)) problems.Add($"{relative}: a PEM private-key block");
        }

        Assert.True(problems.Count == 0, "Private key material in files git would commit:\n" + string.Join("\n", problems));
        Assert.True(testKeySightings >= 1,
            "The TEST key in the vector fixture was not found, so the scan is not seeing the repository's files.");
    }

    /// <summary>The opening characters of the TEST-ONLY key, read from the vector fixture itself.</summary>
    private static string TestKeyStart()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(VectorFixture.FilePath));
        var key = document.RootElement.GetProperty("testOnlyPrivateKeyPkcs8").GetString()!;
        Assert.True(key.Length > CompareChars);
        return key[..CompareChars];
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                Assert.True(Directory.Exists(Path.Combine(dir.FullName, "src", "PropertyGpsApi")),
                    $"{dir.FullName} does not look like the PropertyGpsApi repository.");
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The test is not running inside a git working tree.");
    }
}

/// <summary>Asks git which files it would commit. Needs git; fails, never skips, without it.</summary>
internal static class GitFiles
{
    public static IReadOnlyList<string> WouldCommit(string root)
    {
        var tried = new List<string>();
        foreach (var git in Candidates())
        {
            tried.Add(git);
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
                start.ArgumentList.Add(arg);

            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (Win32Exception)
            {
                continue;   // not at this location
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                _ = stderr.Result;
                if (process.ExitCode != 0) continue;   // e.g. a damaged install's launcher

                return stdout.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        throw new InvalidOperationException(
            "git could not be run (tried: " + string.Join(", ", tried) + "). This check lists the files git " +
            "would commit; put a working git.exe on PATH.");
    }

    private static IEnumerable<string> Candidates()
    {
        yield return "git";
        if (!OperatingSystem.IsWindows()) yield break;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var relative in new[] { @"Git\cmd\git.exe", @"Git\mingw64\bin\git.exe", @"Git\mingw64\libexec\git-core\git.exe" })
            yield return Path.Combine(programFiles, relative);
    }
}
