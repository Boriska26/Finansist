using System.Reflection;
using Xunit;

// Restricted environments cannot create the TCP socket required by VSTest.
// This in-process fallback runs this project's Fact and InlineData Theory tests.
// The same tests remain discoverable by standard `dotnet test` outside the sandbox.
var passed = 0;
var failed = 0;
foreach (var type in Assembly.GetExecutingAssembly().GetTypes().OrderBy(type => type.FullName))
{
    foreach (var method in type.GetMethods().OrderBy(method => method.Name))
    {
        var fact = method.GetCustomAttribute<FactAttribute>();
        if (fact is null) continue;
        if (fact.Skip is not null || fact.Timeout != 0 || typeof(IAsyncLifetime).IsAssignableFrom(type))
            throw new NotSupportedException("Use VSTest for skip, timeout or async fixture support.");
        var rows = method.GetCustomAttributes<InlineDataAttribute>()
            .SelectMany(attribute => attribute.GetData(method)).ToArray();
        if (rows.Length == 0)
        {
            if (fact is TheoryAttribute) throw new NotSupportedException("Only InlineData theories are supported.");
            rows = [Array.Empty<object>()];
        }
        foreach (var row in rows)
        {
            var name = $"{type.Name}.{method.Name}({string.Join(",", row)})";
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                var result = method.Invoke(instance, row);
                if (result is Task task) await task;
                (instance as IDisposable)?.Dispose();
                instance = null;
                Console.WriteLine($"PASS {name}");
                passed++;
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                Console.Error.WriteLine($"FAIL {name}: {cause.GetType().Name}: {cause.Message}");
                failed++;
            }
            finally { (instance as IDisposable)?.Dispose(); }
        }
    }
}
Console.WriteLine($"Passed: {passed}; failed: {failed}");
return failed == 0 && passed > 0 ? 0 : 1;
