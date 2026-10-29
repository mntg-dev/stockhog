using System.Reflection;
using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Architecture.Tests;

/// <summary>
/// CQRS and DDD conventions, checked with reflection so new features follow the same shape.
/// </summary>
public class ConventionTests
{
    private static readonly Type[] DomainTypes = typeof(Symbol).Assembly.GetTypes();
    private static readonly Type[] ApplicationTypes = typeof(IDispatcher).Assembly.GetTypes();

    [Fact]
    public void Commands_and_queries_are_sealed_records_named_by_their_role()
    {
        var violations =
            from type in ApplicationTypes
            where type.IsClass && !type.IsAbstract
            let isCommand = Implements(type, typeof(ICommand<>))
            let isQuery = Implements(type, typeof(IQuery<>))
            where isCommand || isQuery
            let expectedSuffix = isCommand ? "Command" : "Query"
            where !type.IsSealed || !IsRecord(type) || !type.Name.EndsWith(expectedSuffix, StringComparison.Ordinal)
            select type.FullName;

        Assert.Empty(violations);
    }

    [Fact]
    public void Handlers_are_internal_and_sealed()
    {
        var violations =
            from type in ApplicationTypes
            where type.IsClass && !type.IsAbstract
            where Implements(type, typeof(IRequestHandler<,>)) || Implements(type, typeof(IDomainEventHandler<>))
            where type.IsPublic || type.IsNestedPublic || !type.IsSealed
            select type.FullName;

        Assert.Empty(violations);
    }

    [Fact]
    public void Every_request_has_exactly_one_handler()
    {
        var requests = ApplicationTypes.Where(t => t is { IsClass: true, IsAbstract: false } && Implements(t, typeof(IRequest<>)));
        var handled = ApplicationTypes
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .GroupBy(i => i.GetGenericArguments()[0])
            .ToDictionary(g => g.Key, g => g.Count());

        var violations = requests.Where(r => handled.GetValueOrDefault(r) != 1).Select(r => $"{r.Name}: {handled.GetValueOrDefault(r)} handler(s)");

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_events_are_sealed_records()
    {
        var violations = DomainTypes
            .Where(t => t.IsClass && typeof(IDomainEvent).IsAssignableFrom(t))
            .Where(t => !t.IsSealed || !IsRecord(t))
            .Select(t => t.FullName);

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_state_cannot_be_set_from_outside()
    {
        var violations =
            from type in DomainTypes
            where type.IsClass
            from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            let setter = property.SetMethod
            where setter is { IsPublic: true } && !IsInitOnly(setter)
            select $"{type.Name}.{property.Name}";

        Assert.Empty(violations);
    }

    private static bool Implements(Type type, Type openGeneric) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);

    // The compiler emits a protected/private EqualityContract property for records.
    private static bool IsRecord(Type type) =>
        type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
