using AwesomeAssertions;
using Tdm.Core.Grammar;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Policy.Tests;

public class PolicyEvaluatorTests
{
    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private static TdmSettings Settings(FailurePolicy failurePolicy = FailurePolicy.BestEffort,
        LifecycleMode lifecycle = LifecycleMode.TrackedTeardown, params DomainSettings[] domains) => new()
    {
        Run = new RunSettings { FailurePolicy = failurePolicy, Lifecycle = lifecycle },
        Domains = [.. domains],
    };

    private static PolicyDocument PolicyWith(string environmentName, EnvironmentPolicy env) => new()
    {
        Environments = new Dictionary<string, EnvironmentPolicy>(StringComparer.OrdinalIgnoreCase) { [environmentName] = env },
    };

    private static readonly Func<string, string?> NoEnv = _ => null;

    [Fact]
    public void UnknownEnvironment_SingleViolation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy());
        var result = PolicyEvaluator.Evaluate(policy, "prod", Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("UnknownEnvironment");
    }

    [Fact]
    public void AllowedLifecycles_RunLifecycleNotPermitted_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { AllowedLifecycles = [LifecycleMode.TrackedTeardown] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.Persistent), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("AllowedLifecycles");
    }

    [Fact]
    public void AllowedLifecycles_ScenarioOverrideNotPermitted_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { AllowedLifecycles = [LifecycleMode.TrackedTeardown] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  @persistent\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.TrackedTeardown), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Message.Should().Contain("Persistent");
    }

    [Fact]
    public void AllowedLifecycles_Permitted_NoViolation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { AllowedLifecycles = [LifecycleMode.TrackedTeardown] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.TrackedTeardown), null, NoEnv);
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void RequireFailurePolicyAtLeast_Weaker_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { RequireFailurePolicyAtLeast = FailurePolicy.FailObject });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev", Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(failurePolicy: FailurePolicy.BestEffort), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("RequireFailurePolicyAtLeast");
    }

    [Fact]
    public void RequireFailurePolicyAtLeast_Stricter_NoViolation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { RequireFailurePolicyAtLeast = FailurePolicy.FailObject });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev", Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(failurePolicy: FailurePolicy.FailRun), null, NoEnv);
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void MaxBulkRowsPerStep_Exceeded_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { MaxBulkRowsPerStep = 100 });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given 500 Products exist"), Settings(), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("MaxBulkRowsPerStep");
    }

    [Fact]
    public void MaxCreatedRowsPerRun_SumsAcrossSteps_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { MaxCreatedRowsPerRun = 600 });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev", Plan("""
            Feature: F
              Scenario: S
                Given 500 Products exist
                And 200 Orders exist
            """), Settings(), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("MaxCreatedRowsPerRun");
    }

    [Fact]
    public void ConnectionStringSources_InlineNotAllowed_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { ConnectionStringSources = ["env"] });
        var domain = new DomainSettings { Name = "Orders", ConnectionString = "Data Source=x" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"), Settings(domains: [domain]), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("ConnectionStringSources");
    }

    [Fact]
    public void ConnectionStringSources_EnvAllowed_NoViolation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { ConnectionStringSources = ["env"] });
        var domain = new DomainSettings { Name = "Orders", ConnectionStringName = "OrdersDb" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"), Settings(domains: [domain]), null, NoEnv);
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void BannedEntities_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { BannedEntities = ["PaymentCard"] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a PaymentCard exists"), Settings(), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("BannedEntities");
    }

    [Fact]
    public void RequiredTags_Missing_Violation()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { RequiredTags = ["@seed"] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"), Settings(), null, NoEnv);
        result.Violations.Should().ContainSingle().Which.Rule.Should().Be("RequiredTags");
    }

    [Fact]
    public void RequiredTags_PrefixMatch_Satisfied()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { RequiredTags = ["@seed"] });
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  @seed:42\n  Scenario: S\n    Given a Customer exists"), Settings(), null, NoEnv);
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Override_ValidToken_BypassesViolations()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy
        {
            AllowedLifecycles = [LifecycleMode.TrackedTeardown],
            Override = new PolicyOverride { Kind = "approvalToken", TokenEnv = "TDM_APPROVAL_TOKEN" },
        });
        var env = new Dictionary<string, string?> { ["TDM_APPROVAL_TOKEN"] = "s3cr3t" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.Persistent), "s3cr3t", k => env.GetValueOrDefault(k));

        result.Violations.Should().ContainSingle();
        result.OverrideApplied.Should().BeTrue();
    }

    [Fact]
    public void Override_WrongToken_DoesNotBypass()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy
        {
            AllowedLifecycles = [LifecycleMode.TrackedTeardown],
            Override = new PolicyOverride { Kind = "approvalToken", TokenEnv = "TDM_APPROVAL_TOKEN" },
        });
        var env = new Dictionary<string, string?> { ["TDM_APPROVAL_TOKEN"] = "s3cr3t" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.Persistent), "wrong", k => env.GetValueOrDefault(k));

        result.OverrideApplied.Should().BeFalse();
    }

    [Fact]
    public void NoOverrideConfigured_TokenIgnored()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy { AllowedLifecycles = [LifecycleMode.TrackedTeardown] });
        var env = new Dictionary<string, string?> { ["TDM_APPROVAL_TOKEN"] = "s3cr3t" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"),
            Settings(lifecycle: LifecycleMode.Persistent), "s3cr3t", k => env.GetValueOrDefault(k));

        result.OverrideApplied.Should().BeFalse();
    }

    [Fact]
    public void NoViolations_OverrideNeverApplied()
    {
        var policy = PolicyWith("shared-dev", new EnvironmentPolicy
        {
            Override = new PolicyOverride { Kind = "approvalToken", TokenEnv = "TDM_APPROVAL_TOKEN" },
        });
        var env = new Dictionary<string, string?> { ["TDM_APPROVAL_TOKEN"] = "s3cr3t" };
        var result = PolicyEvaluator.Evaluate(policy, "shared-dev",
            Plan("Feature: F\n  Scenario: S\n    Given a Customer exists"), Settings(), "s3cr3t", k => env.GetValueOrDefault(k));

        result.Violations.Should().BeEmpty();
        result.OverrideApplied.Should().BeFalse(); // nothing to override
    }
}
