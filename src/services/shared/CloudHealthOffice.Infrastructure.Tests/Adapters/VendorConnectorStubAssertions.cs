using System.Reflection;

namespace CloudHealthOffice.Infrastructure.Tests.Adapters;

/// <summary>
/// ADR 027 guard for vendor connector templates (QNXT, Facets, HealthEdge):
/// until an engagement implements a method it must fail explicitly with
/// <see cref="NotImplementedException"/>. It must never return synthetic
/// success, an empty result or the reference backend's data.
/// </summary>
/// <remarks>
/// Service test projects call this for each stub class. When a connector
/// method is implemented for an engagement, list it in
/// <c>implementedMethods</c> and cover it with that domain's conformance
/// suite instead.
/// </remarks>
public static class VendorConnectorStubAssertions
{
    /// <summary>
    /// Invokes every method of <typeparamref name="TContract"/> on
    /// <paramref name="connector"/> with default-constructed arguments and
    /// returns the names of methods that did not fail with
    /// <see cref="NotImplementedException"/>. An empty list means the
    /// template fails explicitly everywhere.
    /// </summary>
    public static async Task<IReadOnlyList<string>> MethodsThatDoNotFailExplicitlyAsync<TContract>(
        TContract connector, params string[] implementedMethods)
        where TContract : class
    {
        var offenders = new List<string>();
        foreach (var method in typeof(TContract).GetMethods().Where(m => !m.IsSpecialName))
        {
            if (implementedMethods.Contains(method.Name))
                continue;

            var args = method.GetParameters().Select(DefaultArgument).ToArray();
            try
            {
                var result = method.Invoke(connector, args);
                if (result is Task task)
                    await task;
                offenders.Add($"{method.Name} returned instead of throwing NotImplementedException");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
            {
            }
            catch (NotImplementedException)
            {
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                offenders.Add($"{method.Name} threw {inner.GetType().Name} instead of NotImplementedException");
            }
        }

        return offenders;
    }

    private static object? DefaultArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(CancellationToken))
            return CancellationToken.None;
        if (type == typeof(string))
            return "conformance-probe";
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        if (Nullable.GetUnderlyingType(type) is not null)
            return null;
        return type.GetConstructor(Type.EmptyTypes) is not null
            ? Activator.CreateInstance(type)
            : null;
    }
}
