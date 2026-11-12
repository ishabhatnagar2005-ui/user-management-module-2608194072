// Host the real API with a generated database; drop only the owned test database on disposal.
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Tests;
public sealed class TestApplication : WebApplicationFactory<Program>
{
    public string DatabaseName { get; } = "um_test_" + Guid.NewGuid().ToString("N");
    public string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    public Dictionary<string, string?> Settings { get; }
    public bool DependencyUnavailable { get; set; }
    // Keep synthetic credentials private to each host and expose direct service access via Services.
    public TestApplication()
    {
        Settings = new()
        {
            ["Mongo:Uri"] = Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27017/?replicaSet=umrs",
            ["Mongo:Database"] = DatabaseName, ["Mongo:TimeoutSeconds"] = "1",
            ["Jwt:Secret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), ["Jwt:Issuer"] = "test-api", ["Jwt:Audience"] = "test-client", ["Jwt:ExpiryMinutes"] = "15",
            ["Security:IdentifierKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ["Security:BlindIndexKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Seed:Enabled"] = "true", ["Seed:AdminUsername"] = "test.admin", ["Seed:AdminPassword"] = Password, ["Seed:UserUsername"] = "test.user", ["Seed:UserPassword"] = Password
        };
    }
    // Add only test-assembly gate probes; no extra endpoints enter the production API.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var setting in Settings) builder.UseSetting(setting.Key, setting.Value);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(AuthenticationGateController).Assembly);
            services.RemoveAll<MongoStore>();
            services.AddTransient(sp =>
            {
                var values = new Dictionary<string, string?>(Settings);
                if (DependencyUnavailable) values["Mongo:Uri"] = "mongodb://127.0.0.1:1/";
                return new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            });
        });
    }
    // Force host startup for direct service tests without bypassing migration or seeding.
    public T Service<T>() where T : notnull { _ = Server; return Services.GetRequiredService<T>(); }
    // Clean up only this generated test database; never the shared development database.
    public override async ValueTask DisposeAsync()
    {
        if (!DatabaseName.StartsWith("um_test_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
        var store = new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(Settings).Build());
        await store.Client.DropDatabaseAsync(DatabaseName);
        await base.DisposeAsync();
    }
}
