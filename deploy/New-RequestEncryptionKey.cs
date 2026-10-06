// Generates one RSA-3072 key pair for request-body encryption (pgps-body/1).
//
//   dotnet run D:\PropertyGpsApi\deploy\New-RequestEncryptionKey.cs -- <name> <folder> [--test-only]
//
// Writes, into <folder>:
//   <name>-<kid>.private.b64   the PRIVATE key, one line of base64 PKCS#8 DER - what
//                              RequestEncryption__Keys__0__PrivateKey expects. Never commit,
//                              email or paste it anywhere.
//   <name>-<kid>.public.pem    the PUBLIC key, which goes into the app
//                              (lib/services/http/request_encryption_keys.dart).
// Prints only the kid and the public key.
//
// The folder is locked BEFORE anything is generated: its access list is REPLACED by exactly
// three entries - the current user, Administrators and SYSTEM, full control, inherited by
// everything inside - with inheritance from the drive turned off. Any other entry an existing
// folder carried (another user, Users, Authenticated Users) is removed. The list is then read
// back, and the tool refuses unless only those three can reach the folder; the private key file
// is checked the same way once written. D:\ grants every signed-in user modify rights, so a key
// written to a plain folder would be readable by all of them.
//
// Refuses a folder inside a git working tree - a .git folder, or the .git FILE that marks a
// worktree or a submodule - always, --test-only included: the TEST pair belongs in a scratch
// folder such as D:\temp\pgps-test-key, and the vector generator copies what it needs.
//
// Never overwrites an existing key file.
//
// The kid is the first 8 bytes of SHA-256 over the public key's SubjectPublicKeyInfo DER, in
// lower-case hex - the same value the app and the API derive, so nothing has to be configured.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run New-RequestEncryptionKey.cs -- <name> <folder> [--test-only]");
    return 2;
}

var name = args[0];
var dir = Path.GetFullPath(args[1]);
var testOnly = args.Contains("--test-only");

if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
{
    Console.Error.WriteLine("name must be a plain file-name prefix, e.g. local or testapps");
    return 2;
}

for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
{
    var dotGit = Path.Combine(d.FullName, ".git");
    if (Directory.Exists(dotGit) || File.Exists(dotGit))
    {
        Console.Error.WriteLine($"Refusing: {dir} is inside a git working tree ({d.FullName}). Use a folder outside every repository, e.g. D:\\PropertyGpsKeys.");
        return 3;
    }
}

Directory.CreateDirectory(dir);

SecurityIdentifier[] allowed = [];
if (OperatingSystem.IsWindows())
{
    allowed =
    [
        WindowsIdentity.GetCurrent().User!,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
    ];

    var problem = Acl.Lock(new DirectoryInfo(dir), allowed);
    if (problem is not null)
    {
        Console.Error.WriteLine($"Refusing: could not lock {dir} ({problem}). Nothing was written.");
        return 4;
    }
}

using var rsa = RSA.Create(3072);
var spki = rsa.ExportSubjectPublicKeyInfo();
var kid = Convert.ToHexStringLower(SHA256.HashData(spki).AsSpan(0, 8));
var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

var privatePath = Path.Combine(dir, $"{name}-{kid}.private.b64");
var publicPath = Path.Combine(dir, $"{name}-{kid}.public.pem");
if (File.Exists(privatePath) || File.Exists(publicPath))
{
    Console.Error.WriteLine($"Refusing: a key file for {name}-{kid} already exists in {dir}. Nothing was written.");
    return 5;
}

WriteNew(privatePath, Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));

if (OperatingSystem.IsWindows())
{
    var problem = Acl.Check(new FileInfo(privatePath).GetAccessControl(), allowed, requireProtected: false);
    if (problem is not null)
    {
        File.Delete(privatePath);
        Console.Error.WriteLine($"Refusing: the private key file did not inherit the locked access list ({problem}). It was deleted.");
        return 4;
    }
}

WriteNew(publicPath, publicPem.ReplaceLineEndings("\n") + "\n");

Console.WriteLine($"name {name}");
Console.WriteLine($"kid  {kid}");
Console.WriteLine(publicPem);
Console.WriteLine($"Private key written to {privatePath}");
Console.WriteLine(testOnly
    ? "TEST ONLY: this pair is for the test vector and must never be configured on a server."
    : "Never commit, email or paste the private key file. Keep a copy in BBMP's secret store.");
return 0;

// UTF-8 without a BOM, and never over an existing file.
static void WriteNew(string path, string text)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
}

[SupportedOSPlatform("windows")]
static class Acl
{
    /// <summary>Replaces the folder's access list, then reads it back. Null when it is locked.</summary>
    public static string? Lock(DirectoryInfo folder, SecurityIdentifier[] allowed)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in allowed)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

        try
        {
            folder.SetAccessControl(security);
            return Check(folder.GetAccessControl(), allowed, requireProtected: true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return e.GetType().Name;
        }
    }

    /// <summary>
    /// Null when only <paramref name="allowed"/> can reach the object: every Allow entry, explicit
    /// or inherited, names one of them, and so does the owner (who can always rewrite the list).
    /// </summary>
    public static string? Check(FileSystemSecurity security, SecurityIdentifier[] allowed, bool requireProtected)
    {
        if (requireProtected && !security.AreAccessRulesProtected)
            return "inheritance from the parent is still on";

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !allowed.Contains(owner))
            return $"it is owned by {owner?.Value ?? "nobody"}";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
                return $"{rule.IdentityReference.Value} still has access";
        }

        return null;
    }
}
