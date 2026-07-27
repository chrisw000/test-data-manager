using System.Reflection;

namespace Tdm.Host;

/// <summary>
/// `tdm init` (W1-D5): scaffolds an annotated tdm.settings.json, a starter feature
/// exercising create/load, a .gitignore, and a CI workflow snippet. With --agents (W5-P6)
/// it also scaffolds the agent-kit (AGENTS.md + skills/) for agentic coders, substituting
/// the domain name for the kit's placeholders. Flags-only; never overwrites an existing
/// file — each file is reported as written or skipped.
/// </summary>
internal static class InitScaffolder
{
    /// <summary>The placeholder domain name the embedded agent-kit templates carry.</summary>
    internal const string DomainPlaceholder = "YourDomain";

    /// <summary>Logical-name prefix of the embedded agent-kit resources (see Tdm.Host.csproj).</summary>
    private const string KitResourcePrefix = "agent-kit/";

    public static int Execute(string directory, string? domain, string? package, bool agents = false)
    {
        var domainName = domain ?? "MyDomain";
        Directory.CreateDirectory(directory);

        WriteIfAbsent(Path.Combine(directory, "tdm.settings.json"), SettingsTemplate(domainName, package));
        WriteIfAbsent(Path.Combine(directory, "features", "getting-started.feature"), FeatureTemplate(domainName));
        WriteIfAbsent(Path.Combine(directory, ".gitignore"), GitIgnoreTemplate);
        WriteIfAbsent(Path.Combine(directory, ".github", "workflows", "tdm-validate.yml"), CiTemplate);

        if (agents)
        {
            ScaffoldAgentKit(directory, domainName);
        }

        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  1. Drop your domain data assemblies into ./plugins/{domainName}");
        Console.WriteLine("     (or configure plugins.acquisition: \"NuGet\" + plugins.feeds in tdm.settings.json).");
        Console.WriteLine("  2. Adjust the starter feature to your entities.");
        Console.WriteLine("  3. Run: tdm validate");
        if (!agents)
        {
            Console.WriteLine();
            Console.WriteLine("Tip: add --agents to also scaffold AGENTS.md + skills/ for agentic coders.");
        }
        Console.WriteLine("Docs: https://github.com/chrisw000/test-data-manager");
        return 0;
    }

    /// <summary>
    /// Writes the embedded agent-kit into <paramref name="directory"/>, substituting the
    /// domain name for the templates' placeholders. Single-sourced from <c>agent-kit/</c>
    /// (embedded resources), so it cannot drift from the docs site's rendered copies.
    /// </summary>
    internal static IReadOnlyList<string> ScaffoldAgentKit(string directory, string domain)
    {
        var written = new List<string>();
        var assembly = typeof(InitScaffolder).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(KitResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            var relative = resource.Substring(KitResourcePrefix.Length)
                .Replace('/', Path.DirectorySeparatorChar);
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded agent-kit resource missing: {resource}");
            using var reader = new StreamReader(stream);
            var content = Substitute(reader.ReadToEnd(), domain);
            var target = Path.Combine(directory, relative);
            WriteIfAbsent(target, content);
            written.Add(target);
        }
        return written;
    }

    /// <summary>Replaces the kit's domain placeholders (PascalCase and lowercase) with the target domain.</summary>
    internal static string Substitute(string content, string domain) => content
        .Replace(DomainPlaceholder, domain, StringComparison.Ordinal)
        .Replace(DomainPlaceholder.ToLowerInvariant(), domain.ToLowerInvariant(), StringComparison.Ordinal);

    private static void WriteIfAbsent(string path, string content)
    {
        if (File.Exists(path))
        {
            Console.WriteLine($"  skipped (exists): {path}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Console.WriteLine($"  written: {path}");
    }

    private static string SettingsTemplate(string domain, string? package) => $$"""
        {
          // Test Data Manager settings — full reference:
          // https://github.com/chrisw000/test-data-manager (docs/design/compatibility.md, docs-site/)
          "run": {
            "name": "{{domain.ToLowerInvariant()}}-seed",
            // BestEffort: log failures and continue | FailObject: skip the failed object | FailRun: abort
            "failurePolicy": "BestEffort",
            // Persistent: rows stay | Transactional: rolled back | TrackedTeardown: deleted at scenario end
            "lifecycle": "TrackedTeardown",
            "defaultSeed": 1,
            "featurePaths": ["features/**/*.feature"],
            "outputPath": "./output"
          },
          "plugins": {
            // Folder: assemblies already on disk (./plugins/{name} or domains[].pluginPath)
            // NuGet:  restore domains[].package from feeds below; pinned in tdm.plugins.lock.json
            "acquisition": "Folder"
            // "feeds": [ { "url": "https://nuget.example.internal/v3/index.json" } ]
          },
          "domains": [
            {
              "name": "{{domain}}",
        {{(package is null
            ? $"      // \"package\": \"Acme.{domain}.Data.Persistence\",  // NuGet package id of the domain data assembly"
            : $"      \"package\": \"{package}\",")}}
              "provider": "Sqlite",                    // Sqlite | SqlServer
              "connectionString": "Data Source=./output/{{domain.ToLowerInvariant()}}.db",
              "conventionProfile": "modern",           // modern | legacy | a custom profile
              "persistence": "RepositoryFirst",        // RepositoryFirst | DbContextOnly | RepositoryOnly
              "ensureCreated": true                    // create schema on first use — local/demo databases only
            }
          ],
          "entities": {
            // Per-entity overrides, e.g.:
            // "Product": { "naturalKey": "Sku", "requireRepository": false }
          }
        }

        """;

    private static string FeatureTemplate(string domain) => $"""
        Feature: Getting started with {domain} seeding
          A first TDM feature: create an entity, then verify it exists.
          Rename "Customer" to one of your entities — `tdm list-entities` shows what resolved.

          Scenario: Create and verify a customer
            Given a Customer exists with name "Acme Ltd"
            Then a Customer "Acme Ltd" should exist

          Scenario: Bulk data
            Given 25 Customers exist
            Then 25 Customers should exist

        """;

    private const string GitIgnoreTemplate = """
        # TDM outputs
        output/
        plugins/

        # .NET
        bin/
        obj/

        """;

    private const string CiTemplate = """
        # TDM validate gate — fails the build on grammar errors, unresolvable entities,
        # or write-repository policy violations, before any data exists.
        name: TDM validate

        on:
          pull_request:

        jobs:
          tdm-validate:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: 10.0.x
              - name: Install TDM
                run: dotnet tool install --global Tdm.Tool
              # Populate ./plugins/{domain} here (NuGet restore of your domain data package),
              # or configure plugins.acquisition: "NuGet" in tdm.settings.json.
              - name: Validate
                run: tdm validate --settings tdm.settings.json --report sarif=output/tdm.sarif
              - name: Upload SARIF (PR annotations)
                if: always() && hashFiles('output/tdm.sarif') != ''
                uses: github/codeql-action/upload-sarif@v3
                with:
                  sarif_file: output/tdm.sarif
                  category: tdm

        """;
}
