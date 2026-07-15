using AwesomeAssertions;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Policy.Tests;

public class PolicyDocumentTests
{
    [Fact]
    public void Load_ParsesCommentsAndTrailingCommasAndEnums()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // full example
              "$schema": "https://example/tdm.policy.schema.json",
              "policyVersion": 1,
              "environments": {
                "shared-dev": {
                  "allowedLifecycles": ["Transactional", "TrackedTeardown"],
                  "requireFailurePolicyAtLeast": "FailObject",
                  "maxBulkRowsPerStep": 10000,
                  "maxCreatedRowsPerRun": 100000,
                  "connectionStringSources": ["env"],
                  "bannedEntities": ["PaymentCard"],
                  "requiredTags": ["@seed"],
                  "override": { "kind": "approvalToken", "tokenEnv": "TDM_APPROVAL_TOKEN" },
                },
              },
            }
            """);
        try
        {
            var policy = PolicyDocument.Load(path);
            policy.PolicyVersion.Should().Be(1);
            var env = policy.Environments.Should().ContainKey("shared-dev").WhoseValue;
            env.AllowedLifecycles.Should().Equal(LifecycleMode.Transactional, LifecycleMode.TrackedTeardown);
            env.RequireFailurePolicyAtLeast.Should().Be(FailurePolicy.FailObject);
            env.MaxBulkRowsPerStep.Should().Be(10000);
            env.MaxCreatedRowsPerRun.Should().Be(100000);
            env.ConnectionStringSources.Should().Equal("env");
            env.BannedEntities.Should().Equal("PaymentCard");
            env.RequiredTags.Should().Equal("@seed");
            env.Override.Should().NotBeNull();
            env.Override!.Kind.Should().Be("approvalToken");
            env.Override.TokenEnv.Should().Be("TDM_APPROVAL_TOKEN");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Environments_LookupIsCaseInsensitive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "environments": { "Shared-Dev": {} } }""");
        try
        {
            var policy = PolicyDocument.Load(path);
            policy.Environments.Should().ContainKey("shared-dev");
        }
        finally { File.Delete(path); }
    }
}
