using System.Net;
using ConCore.Targets;
using ConInfrastructure.NameResolution;
using ConPersistence.Config;
using ConWatch.NameResolution;

namespace ConWatch.Tests;

public class SystemResolverReconcilerTests
{
    private static SystemResolver Found(string address, bool docker = false) => new(IPAddress.Parse(address), docker);
    private static readonly IReadOnlySet<string> NoneRetired = new HashSet<string>();

    private static TargetDto Managed(string address, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(), Name = $"Your resolver {address}", Address = address, Profile = "Resolver",
        Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true, Enabled = enabled,
    };

    private static readonly TargetDto Router = new() { Id = Guid.NewGuid(), Name = "Router", Address = "10.1.10.1", Profile = "Internal" };

    [Fact]
    public void A_found_address_becomes_an_enabled_managed_Resolver_target()
    {
        var plan = SystemResolverReconciler.Reconcile([Router], [Found("10.1.10.1")], watchActive: true, NoneRetired);

        Assert.True(plan.Changed);
        var created = Assert.Single(plan.Targets, t => t.SystemManaged);
        Assert.Equal("Your resolver 10.1.10.1", created.Name);
        Assert.Equal("10.1.10.1", created.Address);
        Assert.Equal("Resolver", created.Profile);
        Assert.Equal("dns", created.Probe);
        Assert.Equal(TargetScope.Resolver, created.Scope);
        Assert.Null(created.Parent);
        Assert.True(created.Enabled);
        Assert.False(created.Critical);
        Assert.Contains(plan.Targets, t => t.Name == "Router"); // untouched
    }

    [Fact]
    public void Dockers_resolver_is_named_as_such_and_a_name_collision_gets_a_suffix()
    {
        var taken = new TargetDto { Id = Guid.NewGuid(), Name = "Docker's resolver", Address = "203.0.113.5", Profile = "Public" };
        var plan = SystemResolverReconciler.Reconcile([taken], [Found("127.0.0.11", docker: true)], true, NoneRetired);
        Assert.Equal("Docker's resolver 2", Assert.Single(plan.Targets, t => t.SystemManaged).Name);
    }

    [Fact]
    public void A_resolver_whose_name_is_taken_gets_a_numeric_suffix()
    {
        var taken = new TargetDto { Id = Guid.NewGuid(), Name = "Your resolver 10.0.0.53", Address = "203.0.113.5", Profile = "Public" };
        var plan = SystemResolverReconciler.Reconcile([taken], [Found("10.0.0.53")], true, NoneRetired);
        Assert.Equal("Your resolver 10.0.0.53 2", Assert.Single(plan.Targets, t => t.SystemManaged).Name);
    }

    [Fact]
    public void A_hand_made_dns_target_at_the_address_is_left_alone_and_logged()
    {
        var handMade = new TargetDto
        {
            Id = Guid.NewGuid(), Name = "My DNS", Address = "10.1.10.53", Profile = "Resolver",
            Probe = "dns", Scope = TargetScope.Resolver,
        };

        var plan = SystemResolverReconciler.Reconcile([handMade], [Found("10.1.10.53")], true, NoneRetired);

        Assert.False(plan.Changed);
        Assert.Single(plan.Targets);
        Assert.Equal("skipped 10.1.10.53: a target with a dns probe already exists at that address", Assert.Single(plan.Log));
    }

    [Fact]
    public void Nothing_is_created_while_the_watch_is_off()
    {
        var plan = SystemResolverReconciler.Reconcile([Router], [Found("10.1.10.1")], watchActive: false, NoneRetired);
        Assert.False(plan.Changed);
        Assert.DoesNotContain(plan.Targets, t => t.SystemManaged);
    }

    [Fact]
    public void A_vanished_address_deletes_the_managed_target_and_remembers_nothing()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([Router, managed], [Found("10.1.10.2")], true, NoneRetired);

        Assert.True(plan.Changed);
        Assert.Equal("10.1.10.2", Assert.Single(plan.Targets, t => t.SystemManaged).Address);
        Assert.Contains(plan.Targets, t => t.Name == "Router");
        Assert.Empty(plan.Retired);
        Assert.Contains("deleted Your resolver 10.1.10.1", plan.Log);
    }

    [Fact]
    public void A_disabled_managed_target_whose_address_is_gone_is_deleted_too()
    {
        var retiredByUs = Managed("10.1.10.1", enabled: false);
        var byAdmin = Managed("10.1.10.2", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([retiredByUs, byAdmin], [Found("10.1.10.3")], true, new HashSet<string> { "10.1.10.1" });

        Assert.True(plan.Changed);
        Assert.Equal("10.1.10.3", Assert.Single(plan.Targets).Address);
        Assert.Empty(plan.Retired); // the entry goes with the target
    }

    [Fact]
    public void A_vanished_address_is_deleted_even_while_the_watch_is_off()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([managed], [Found("10.1.10.2")], false, NoneRetired);
        Assert.Empty(plan.Targets); // nothing is created while the watch is off
        Assert.Empty(plan.Retired);
    }

    /// <summary>Final review I1: a link flap, sleep/resume or a throwing read can report
    /// no resolvers for a moment; that is no information, not "every resolver is gone".</summary>
    [Fact]
    public void An_empty_OS_list_is_no_information_and_changes_nothing()
    {
        var enabled = Managed("10.1.10.1");
        var byAdmin = Managed("10.1.10.2", enabled: false);
        var retiredByUs = Managed("10.1.10.3", enabled: false);
        var retired = new HashSet<string> { "10.1.10.3" };

        foreach (var watchActive in new[] { true, false })
        {
            var plan = SystemResolverReconciler.Reconcile([Router, enabled, byAdmin, retiredByUs], [], watchActive, retired);

            Assert.False(plan.Changed);
            Assert.Equal([Router, enabled, byAdmin, retiredByUs], plan.Targets);
            Assert.Equal(retired, plan.Retired);
            Assert.Equal(["no resolvers found: keeping targets as they are"], plan.Log);
        }
    }

    [Fact]
    public void A_returning_address_is_re_enabled_only_when_the_service_retired_it()
    {
        var retiredByUs = Managed("10.1.10.1", enabled: false);
        var byAdmin = Managed("10.1.10.2", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([retiredByUs, byAdmin],
            [Found("10.1.10.1"), Found("10.1.10.2")], true, new HashSet<string> { "10.1.10.1" });

        Assert.True(plan.Changed);
        Assert.True(plan.Targets.Single(t => t.Id == retiredByUs.Id).Enabled);
        Assert.False(plan.Targets.Single(t => t.Id == byAdmin.Id).Enabled);
        Assert.DoesNotContain("10.1.10.1", plan.Retired);
    }

    [Fact]
    public void Turning_the_watch_off_retires_every_managed_target_and_on_restores_them()
    {
        var a = Managed("10.1.10.1");
        var b = Managed("10.1.10.2");
        var off = SystemResolverReconciler.Reconcile([a, b], [Found("10.1.10.1"), Found("10.1.10.2")], false, NoneRetired);
        Assert.All(off.Targets.Where(t => t.SystemManaged), t => Assert.False(t.Enabled));
        Assert.Equal(new HashSet<string> { "10.1.10.1", "10.1.10.2" }, off.Retired);

        var on = SystemResolverReconciler.Reconcile(off.Targets, [Found("10.1.10.1"), Found("10.1.10.2")], true, off.Retired);
        Assert.All(on.Targets.Where(t => t.SystemManaged), t => Assert.True(t.Enabled));
        Assert.Empty(on.Retired);
    }

    [Fact]
    public void A_steady_state_is_not_a_change()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([Router, managed], [Found("10.1.10.1")], true, NoneRetired);
        Assert.False(plan.Changed);
        Assert.Same(managed, plan.Targets.Single(t => t.SystemManaged));
    }

    [Fact]
    public void An_admin_re_enabled_target_clears_its_retired_entry()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([managed], [Found("10.1.10.1")], true, new HashSet<string> { "10.1.10.1" });

        Assert.True(plan.Changed);
        Assert.Same(managed, plan.Targets.Single());
        Assert.DoesNotContain("10.1.10.1", plan.Retired);
    }

    [Fact]
    public void Addresses_match_case_insensitively_and_a_retired_entry_with_no_target_is_dropped()
    {
        var managed = Managed("fe80::1", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([managed], [Found("FE80::1")], true, new HashSet<string> { "FE80::1", "10.9.9.9" });
        Assert.True(plan.Targets.Single().Enabled);
        Assert.Empty(plan.Retired);
    }
}
