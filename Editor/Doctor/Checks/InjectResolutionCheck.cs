using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Flags <c>[Inject]</c> members whose type cannot be satisfied by anything the
    /// Doctor can see: not a <c>RuntimeSubsystem</c>, not an interface implemented by
    /// one, and never mentioned in a <c>RegisterService/BindService/RegisterFactory</c>
    /// call in scanned sources. Warning severity — manual registrations made by SDK
    /// forks or at runtime are invisible to static analysis.
    /// </summary>
    public class InjectResolutionCheck : IDoctorCheck
    {
        public string Id => "inject-unresolvable";
        public string Description => "[Inject] members of types with no visible registration";

        private static readonly Regex RegistrationCall =
            new Regex(@"\b(?:RegisterService|BindService|RegisterFactory)\s*<\s*([\w\.]+)");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            // Text scan + managed reflection only — safe and worthwhile off the main thread.
            await Awaitable.BackgroundThreadAsync();

            var issues = new List<DoctorIssue>();

            // Types registered explicitly somewhere in scanned source.
            var registeredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in context.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var line in source.Lines)
                {
                    foreach (Match m in RegistrationCall.Matches(line))
                    {
                        var name = m.Groups[1].Value;
                        registeredNames.Add(name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name);
                    }
                }
            }

            var subsystemBase = FindType("Molca.RuntimeSubsystem");
            var injectAttr = FindType("Molca.InjectAttribute");
            if (subsystemBase == null || injectAttr == null)
                return issues;

            var relevantAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Where(a =>
                {
                    var n = a.GetName().Name;
                    // Test assemblies declare deliberately-unresolvable fixtures.
                    if (n.EndsWith(".Tests", StringComparison.Ordinal))
                        return false;
                    return n.StartsWith("Molca", StringComparison.Ordinal) || n.StartsWith("Assembly-CSharp", StringComparison.Ordinal);
                })
                .ToList();

            var allTypes = relevantAssemblies.SelectMany(SafeGetTypes).ToList();
            var subsystemTypes = allTypes.Where(t => subsystemBase.IsAssignableFrom(t) && !t.IsAbstract).ToList();

            foreach (var type in allTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var member in type.GetFields(flags).Cast<MemberInfo>().Concat(type.GetProperties(flags)))
                {
                    var attr = member.GetCustomAttributes().FirstOrDefault(a => a.GetType() == injectAttr);
                    if (attr == null)
                        continue;

                    // Optional injections ([Inject(false)]) stay null by contract —
                    // an unresolvable type is not a finding there.
                    var requiredProp = injectAttr.GetProperty("Required");
                    if (requiredProp != null && requiredProp.GetValue(attr) is bool required && !required)
                        continue;

                    var memberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;

                    bool resolvable =
                        subsystemBase.IsAssignableFrom(memberType)
                        || ((memberType.IsInterface || memberType.IsAbstract) && subsystemTypes.Any(memberType.IsAssignableFrom))
                        || registeredNames.Contains(memberType.Name);

                    if (!resolvable)
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"[Inject] {type.Name}.{member.Name} of type {memberType.Name}: no subsystem or RegisterService/BindService registration found for it."));
                    }
                }
            }
            return issues;
        }

        private static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetType(fullName))
                .FirstOrDefault(t => t != null);

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
    }
}
