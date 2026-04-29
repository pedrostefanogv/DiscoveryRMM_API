using System.Security.Cryptography;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api;

/// <summary>
/// Handles the --recover-admin maintenance mode: parses CLI args, resets admin credentials, and rebinds admin role.
/// </summary>
internal static class MaintenanceMode
{
    public record Options(
        bool RecoverAdmin,
        bool ShowHelp,
        string Login,
        string? Password,
        bool PasswordFromStdin,
        bool CreateIfMissing,
        bool ResetMfa,
        bool ReactivateUser)
    {
        public static Options Default => new(
            RecoverAdmin: false,
            ShowHelp: false,
            Login: "admin",
            Password: null,
            PasswordFromStdin: false,
            CreateIfMissing: true,
            ResetMfa: true,
            ReactivateUser: true);
    }

    public static bool TryParse(string[] args, out Options options, out string? error)
    {
        options = Options.Default;
        error = null;

        if (!args.Any(a => string.Equals(a, "--recover-admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--recover-admin-help", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        options = options with { RecoverAdmin = true };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--recover-admin-help", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(arg, "--recover-admin", StringComparison.OrdinalIgnoreCase)
                    && args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))))
            {
                options = options with { ShowHelp = true };
                continue;
            }

            if (IsKnownArg(arg))
                continue;

            switch (arg.ToLowerInvariant())
            {
                case "--login":
                    if (i + 1 >= args.Length) { error = "Missing value for --login."; return true; }
                    options = options with { Login = args[++i] };
                    break;
                case "--password":
                    if (i + 1 >= args.Length) { error = "Missing value for --password."; return true; }
                    options = options with { Password = args[++i] };
                    break;
                case "--password-stdin":
                    options = options with { PasswordFromStdin = true };
                    break;
                case "--create-if-missing":
                    options = options with { CreateIfMissing = true };
                    break;
                case "--no-create-if-missing":
                    options = options with { CreateIfMissing = false };
                    break;
                case "--reset-mfa":
                    options = options with { ResetMfa = true };
                    break;
                case "--keep-mfa":
                    options = options with { ResetMfa = false };
                    break;
                case "--reactivate":
                    options = options with { ReactivateUser = true };
                    break;
                case "--no-reactivate":
                    options = options with { ReactivateUser = false };
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Password) && options.PasswordFromStdin)
        {
            error = "Use either --password or --password-stdin, not both.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.Login))
        {
            error = "Login cannot be empty.";
            return true;
        }

        return true;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Discovery API maintenance command: --recover-admin");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/Discovery.Api -- --recover-admin [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --login <value>             Target login (default: admin)");
        Console.WriteLine("  --password <value>          Explicit password (less secure: appears in history)");
        Console.WriteLine("  --password-stdin            Read password from standard input");
        Console.WriteLine("  --create-if-missing         Create the admin account if it does not exist (default)");
        Console.WriteLine("  --no-create-if-missing      Fail if login does not exist");
        Console.WriteLine("  --reset-mfa                 Deactivate MFA keys and require new enrollment (default)");
        Console.WriteLine("  --keep-mfa                  Keep current MFA keys");
        Console.WriteLine("  --reactivate                Re-enable user if inactive (default)");
        Console.WriteLine("  --no-reactivate             Keep current IsActive status");
        Console.WriteLine("  --recover-admin-help        Show this help");
    }

    public static async Task<int> ExecuteAsync(IServiceProvider services, Options options)
    {
        if (!options.RecoverAdmin)
            return 0;

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var mfaKeys = scope.ServiceProvider.GetRequiredService<IUserMfaKeyRepository>();
        var sessions = scope.ServiceProvider.GetRequiredService<IUserSessionRepository>();
        var passwordService = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var password = ResolvePassword(options);
        if (password is null)
        {
            Console.Error.WriteLine("Failed to read password from stdin.");
            return 2;
        }

        var (policyValid, policyReason) = passwordService.ValidatePolicy(password);
        if (!policyValid)
        {
            Console.Error.WriteLine($"Password policy failed: {policyReason}");
            return 2;
        }

        var login = options.Login.Trim();
        var user = await users.GetByLoginAsync(login);
        var created = false;

        if (user is null)
        {
            if (!options.CreateIfMissing)
            {
                Console.Error.WriteLine($"User '{login}' not found. Use --create-if-missing to bootstrap it.");
                return 1;
            }

            user = CreateAdminUser(login, password, passwordService);
            user = await users.CreateAsync(user);
            created = true;
        }

        if (!created)
        {
            user = UpdateExistingUser(user, password, passwordService, options);
            user = await users.UpdateAsync(user);
        }

        if (options.ResetMfa)
            await mfaKeys.DeactivateAllByUserIdAsync(user.Id);

        await sessions.RevokeAllByUserIdAsync(user.Id);
        await EnsureAdminBindingAsync(db, user.Id);

        PrintResult(user, options, created);
        return 0;
    }

    private static User CreateAdminUser(string login, string password, IPasswordService passwordService)
    {
        var salt = passwordService.GenerateSalt();
        var hash = passwordService.HashPassword(password, salt);
        var now = DateTime.UtcNow;

        return new User
        {
            Id = Guid.NewGuid(),
            Login = login,
            Email = string.Equals(login, "admin", StringComparison.OrdinalIgnoreCase)
                ? "admin@local.discovery"
                : $"{login}@local.discovery",
            FullName = "Administrador Recuperado",
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            MfaRequired = true,
            MfaConfigured = false,
            MustChangePassword = true,
            MustChangeProfile = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static User UpdateExistingUser(User user, string password, IPasswordService passwordService, Options options)
    {
        var salt = passwordService.GenerateSalt();
        var hash = passwordService.HashPassword(password, salt);

        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.MfaRequired = true;
        user.MustChangePassword = true;

        if (options.ReactivateUser)
            user.IsActive = true;

        if (options.ResetMfa)
            user.MfaConfigured = false;

        user.UpdatedAt = DateTime.UtcNow;
        return user;
    }

    private static void PrintResult(User user, Options options, bool created)
    {
        Console.WriteLine("Admin recovery completed.");
        Console.WriteLine($"Login: {user.Login}");
        Console.WriteLine($"User created: {(created ? "yes" : "no")}");
        Console.WriteLine($"MFA reset: {(options.ResetMfa ? "yes" : "no")}");
        Console.WriteLine("All active sessions for this user were revoked.");

        if (options.PasswordFromStdin || !string.IsNullOrWhiteSpace(options.Password))
        {
            Console.WriteLine("Password source: provided by operator (not echoed).\n");
        }
        else
        {
            var tempPassword = options.Password ?? "(auto-generated)";
            Console.WriteLine($"Temporary password: {tempPassword}");
            Console.WriteLine("Store it safely. The user must change password on next login.");
        }
    }

    private static async Task EnsureAdminBindingAsync(DiscoveryDbContext db, Guid userId)
    {
        const string adminGroupName = "Administradores";
        const string adminRoleName = "Admin";

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_groups (id, name, description, is_active, created_at, updated_at)
            SELECT gen_random_uuid(), {adminGroupName}, 'Grupo administrativo inicial do sistema', true, NOW(), NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM user_groups WHERE name = {adminGroupName}
            );
        """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_group_memberships (user_id, group_id, joined_at)
            SELECT {userId}, g.id, NOW()
            FROM user_groups g
            WHERE g.name = {adminGroupName}
              AND NOT EXISTS (
                  SELECT 1
                  FROM user_group_memberships ugm
                  WHERE ugm.user_id = {userId}
                    AND ugm.group_id = g.id
              );
        """);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_group_roles (id, group_id, role_id, scope_level, scope_id, assigned_at)
            SELECT gen_random_uuid(), g.id, r.id, 'Global', NULL, NOW()
            FROM user_groups g
            CROSS JOIN roles r
            WHERE g.name = {adminGroupName}
              AND r.name = {adminRoleName}
              AND NOT EXISTS (
                  SELECT 1
                  FROM user_group_roles ugr
                  WHERE ugr.group_id = g.id
                    AND ugr.role_id = r.id
                    AND ugr.scope_level = 'Global'
                    AND ugr.scope_id IS NULL
              );
        """);
    }

    private static string? ResolvePassword(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.Password))
            return options.Password;

        if (options.PasswordFromStdin)
        {
            var line = Console.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }

        return GenerateTemporaryPassword();
    }

    private static string GenerateTemporaryPassword()
    {
        const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lowercase = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string specials = "!@#$%*-_";
        var all = uppercase + lowercase + digits + specials;

        var chars = new List<char>
        {
            uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)],
            lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            specials[RandomNumberGenerator.GetInt32(specials.Length)]
        };

        for (var i = chars.Count; i < 20; i++)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars.ToArray());
    }

    private static bool IsKnownArg(string arg) =>
        string.Equals(arg, "--recover-admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase);
}
