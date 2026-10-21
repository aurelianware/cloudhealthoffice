using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CloudHealthOffice.Architecture.Tests;

/// <summary>
/// Every MongoDB query on a tenant-scoped collection must be scoped to a
/// tenant. A collection is tenant-scoped when its document type declares a
/// <c>TenantId</c> property (directly or through a base type). A method that
/// reads, counts, updates, replaces or deletes in such a collection must refer
/// to the tenant (an identifier containing "tenant": the request's tenant id,
/// a <c>TenantId</c> filter, a tenant-scoped filter helper), directly or
/// through another method of the same class that does. A query that is meant
/// to span tenants (a background job that processes each record under its own
/// tenant, a migration) says so with <c>[CrossTenantQuery("why")]</c> on the
/// method or class.
///
/// <para>
/// This is a source check, so it is a floor, not a proof: it catches the
/// common mistake (a lookup by id alone) and makes every cross-tenant query an
/// explicit, reviewed decision. A method that mentions the tenant without
/// filtering on it passes; code review and the tenant isolation lab tests
/// cover that.
/// </para>
/// </summary>
public class TenantFilterArchitectureTests(ITestOutputHelper output)
{
    internal const string AttributeName = "CrossTenantQuery";

    private static readonly HashSet<string> QueryMethods = new(StringComparer.Ordinal)
    {
        "Find", "FindAsync", "FindSync",
        "Aggregate", "AggregateAsync",
        "CountDocuments", "CountDocumentsAsync", "EstimatedDocumentCount", "EstimatedDocumentCountAsync",
        "Distinct", "DistinctAsync",
        "UpdateOne", "UpdateOneAsync", "UpdateMany", "UpdateManyAsync",
        "ReplaceOne", "ReplaceOneAsync",
        "DeleteOne", "DeleteOneAsync", "DeleteMany", "DeleteManyAsync",
        "FindOneAndUpdate", "FindOneAndUpdateAsync", "FindOneAndReplace", "FindOneAndReplaceAsync",
        "FindOneAndDelete", "FindOneAndDeleteAsync",
        "BulkWrite", "BulkWriteAsync",
        "Watch", "WatchAsync",
        "AsQueryable",
    };

    [Fact]
    public void Every_query_on_a_tenant_scoped_collection_is_scoped_to_a_tenant()
    {
        var violations = Scan(SourceFiles());

        var report = new StringBuilder();
        report.AppendLine($"{violations.Count} MongoDB quer(ies) on tenant-scoped collections with no tenant scoping.");
        report.AppendLine("Filter on TenantId, or mark a deliberate cross-tenant query [CrossTenantQuery(\"why\")]:");
        foreach (var v in violations) report.AppendLine("  " + v);
        Assert.True(violations.Count == 0, report.ToString());
    }

    [Fact]
    public void The_scan_finds_the_repositories()
    {
        // Guards the check itself: if path discovery or parsing broke, it
        // would pass vacuously.
        var stats = Inventory(SourceFiles());
        output.WriteLine($"{stats.TenantScopedTypes} tenant-scoped document types; {stats.CheckedQueries} queries checked.");
        Assert.True(stats.TenantScopedTypes > 50, $"only {stats.TenantScopedTypes} tenant-scoped document types found");
        Assert.True(stats.CheckedQueries > 300, $"only {stats.CheckedQueries} queries checked");
    }

    [Fact]
    public void An_unscoped_lookup_is_reported_and_a_scoped_or_marked_one_is_not()
    {
        const string source = """
            public class Widget { public string Id { get; set; } public string TenantId { get; set; } }
            public class Global { public string Id { get; set; } }
            public class WidgetRepository
            {
                private readonly IMongoCollection<Widget> _widgets;
                private readonly IMongoCollection<Global> _globals;
                public Task<Widget> ById(string id) => _widgets.Find(w => w.Id == id).FirstAsync();
                public Task<Widget> Scoped(string id, string tenantId) => _widgets.Find(w => w.Id == id && w.TenantId == tenantId).FirstAsync();
                public Task<Widget> ViaHelper(string id) => _widgets.Find(Filter(id)).FirstAsync();
                private FilterDefinition<Widget> Filter(string id) => Builders<Widget>.Filter.Eq(w => w.TenantId, CurrentTenant());
                [CrossTenantQuery("nightly job, per-record tenant")]
                public Task<long> CountAll() => _widgets.CountDocumentsAsync(FilterDefinition<Widget>.Empty);
                public Task<Global> Shared(string id) => _globals.Find(g => g.Id == id).FirstAsync();
                public Task Insert(Widget w) => _widgets.InsertOneAsync(w);
            }
            public class GadgetRepository
            {
                private readonly IMongoCollection<Widget> _widgets;
                public Task<List<Widget>> ForTenant(string tenantId) => Query(Builders<Widget>.Filter.Eq(w => w.TenantId, tenantId));
                private Task<List<Widget>> Query(FilterDefinition<Widget> filter) => _widgets.Find(filter).ToListAsync();
            }
            public class LeakyGadgetRepository
            {
                private readonly IMongoCollection<Widget> _widgets;
                public Task<List<Widget>> ForTenant(string tenantId) => Query(Builders<Widget>.Filter.Eq(w => w.TenantId, tenantId));
                public Task<List<Widget>> Everything() => Query(FilterDefinition<Widget>.Empty);
                private Task<List<Widget>> Query(FilterDefinition<Widget> filter) => _widgets.Find(filter).ToListAsync();
            }
            """;

        var violations = Scan([("Fixture.cs", source)]);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("WidgetRepository.ById"));
        Assert.Contains(violations, v => v.Contains("LeakyGadgetRepository.Query"));
    }

    // ── Scanner ─────────────────────────────────────────────────────────

    internal sealed record Stats(int TenantScopedTypes, int CheckedQueries);

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        var root = RepositoryRoot();
        var src = Path.Combine(root, "src");
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("/node_modules/")) continue;
            if (relative.Split('/').Any(s => s.EndsWith(".Tests", StringComparison.Ordinal) || s.EndsWith("Tests", StringComparison.Ordinal) && s != "Tests")) continue;
            yield return (relative, File.ReadAllText(path));
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Directory.Packages.props) not found.");
    }

    private static Stats Inventory(IEnumerable<(string Path, string Text)> files)
    {
        var trees = Parse(files);
        var scoped = TenantScopedTypes(trees);
        var checkedQueries = 0;
        foreach (var (_, tree) in trees)
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                checkedQueries += QueriesIn(type, CollectionMembers(type, scoped), scoped).Count;
        return new Stats(scoped.Count, checkedQueries);
    }

    internal static List<string> Scan(IEnumerable<(string Path, string Text)> files)
    {
        var trees = Parse(files);
        var scoped = TenantScopedTypes(trees);
        var violations = new List<string>();

        foreach (var (path, tree) in trees)
        {
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (HasAttribute(type.AttributeLists)) continue;
                var collections = CollectionMembers(type, scoped);
                var queries = QueriesIn(type, collections, scoped);
                if (queries.Count == 0) continue;

                var aware = TenantAwareMembers(type);
                foreach (var (member, invocation, op) in queries)
                {
                    if (HasAttribute(MemberAttributes(member))) continue;
                    if (aware.Contains(member)) continue;
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    violations.Add($"{path}:{line} {type.Identifier.Text}.{MemberName(member)} → {op}");
                }
            }
        }

        return violations.Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    private static List<(string Path, SyntaxTree Tree)> Parse(IEnumerable<(string Path, string Text)> files)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        return files.Select(f => (f.Path, CSharpSyntaxTree.ParseText(f.Text, options, f.Path))).ToList();
    }

    /// <summary>Names of document types that carry a TenantId, directly or through a base type.</summary>
    private static HashSet<string> TenantScopedTypes(List<(string Path, SyntaxTree Tree)> trees)
    {
        var declaresTenant = new HashSet<string>(StringComparer.Ordinal);
        var bases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (_, tree) in trees)
        {
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var name = type.Identifier.Text;
                var hasTenant = type.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == "TenantId")
                    || type.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "TenantId"))
                    || (type.ParameterList?.Parameters.Any(p => p.Identifier.Text == "TenantId") ?? false);
                if (hasTenant) declaresTenant.Add(name);

                if (type.BaseList is not null)
                {
                    if (!bases.TryGetValue(name, out var set)) bases[name] = set = new(StringComparer.Ordinal);
                    foreach (var b in type.BaseList.Types) set.Add(SimpleName(b.Type));
                }
            }
        }

        var scoped = new HashSet<string>(declaresTenant, StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, set) in bases)
                if (!scoped.Contains(name) && set.Any(scoped.Contains))
                    changed |= scoped.Add(name);
        } while (changed);
        return scoped;
    }

    /// <summary>Fields, properties and locals of type IMongoCollection&lt;T&gt; (or GetCollection&lt;T&gt;) for a tenant-scoped T.</summary>
    private static HashSet<string> CollectionMembers(TypeDeclarationSyntax type, HashSet<string> scoped)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
            if (IsScopedCollection(field.Declaration.Type, scoped))
                foreach (var v in field.Declaration.Variables) names.Add(v.Identifier.Text);

        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
            if (IsScopedCollection(prop.Type, scoped)) names.Add(prop.Identifier.Text);

        if (type.ParameterList is not null)
            foreach (var p in type.ParameterList.Parameters)
                if (p.Type is not null && IsScopedCollection(p.Type, scoped)) names.Add(p.Identifier.Text);

        foreach (var local in type.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (IsScopedCollection(local.Type, scoped))
            {
                foreach (var v in local.Variables) names.Add(v.Identifier.Text);
                continue;
            }
            foreach (var v in local.Variables)
                if (v.Initializer?.Value is { } init && GetCollectionType(init) is { } t && scoped.Contains(t))
                    names.Add(v.Identifier.Text);
        }

        return names;
    }

    private static bool IsScopedCollection(TypeSyntax type, HashSet<string> scoped) =>
        type is GenericNameSyntax { Identifier.Text: "IMongoCollection" or "MongoCollection" } g
            && scoped.Contains(SimpleName(g.TypeArgumentList.Arguments[0]))
        || type is QualifiedNameSyntax q && IsScopedCollection(q.Right, scoped)
        || type is NullableTypeSyntax n && IsScopedCollection(n.ElementType, scoped);

    /// <summary>T of a <c>….GetCollection&lt;T&gt;(…)</c> expression, if it is one.</summary>
    private static string? GetCollectionType(ExpressionSyntax expr)
    {
        var invocation = expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i =>
            (i.Expression as MemberAccessExpressionSyntax)?.Name is GenericNameSyntax { Identifier.Text: "GetCollection" }
            || i.Expression is GenericNameSyntax { Identifier.Text: "GetCollection" });
        var generic = invocation?.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            GenericNameSyntax g => g,
            _ => null,
        };
        return generic is null ? null : SimpleName(generic.TypeArgumentList.Arguments[0]);
    }

    private static List<(SyntaxNode Member, InvocationExpressionSyntax Invocation, string Op)> QueriesIn(
        TypeDeclarationSyntax type, HashSet<string> collections, HashSet<string> scoped)
    {
        var found = new List<(SyntaxNode, InvocationExpressionSyntax, string)>();
        foreach (var invocation in type.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
            var op = access.Name.Identifier.Text;
            if (!QueryMethods.Contains(op)) continue;

            var receiver = access.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var n } => n.Identifier.Text,
                _ => null,
            };
            var direct = receiver is not null && collections.Contains(receiver);
            var inline = access.Expression is InvocationExpressionSyntax
                && GetCollectionType(access.Expression) is { } t && scoped.Contains(t);
            if (!direct && !inline) continue;

            var member = invocation.Ancestors().FirstOrDefault(a =>
                a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                    or PropertyDeclarationSyntax);
            if (member is LocalFunctionStatementSyntax)
                member = member.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax) ?? member;
            if (member is AccessorDeclarationSyntax)
                member = member.Ancestors().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault() ?? member;
            if (member is null) continue;
            if (member.Ancestors().OfType<TypeDeclarationSyntax>().First() != type) continue;
            found.Add((member, invocation, $"{receiver ?? "GetCollection"}.{op}"));
        }
        return found;
    }

    /// <summary>
    /// Members whose body refers to the tenant, directly or by calling a
    /// member of the same type that does (to a fixpoint).
    /// </summary>
    private static HashSet<SyntaxNode> TenantAwareMembers(TypeDeclarationSyntax type)
    {
        var members = type.Members
            .Where(m => m is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax)
            .ToList();
        var byName = members.GroupBy(MemberName).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var aware = new HashSet<SyntaxNode>(members.Where(MentionsTenant));
        // Fields initialised with a tenant-scoped filter count as tenant references too.
        var tenantFields = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(MentionsTenant)
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);

        bool changed;
        do
        {
            changed = false;
            foreach (var m in members)
            {
                if (aware.Contains(m)) continue;
                var identifiers = m.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text);
                if (identifiers.Any(id => tenantFields.Contains(id)
                        || byName.TryGetValue(id, out var callees) && callees.Any(aware.Contains)))
                {
                    changed |= aware.Add(m);
                    continue;
                }

                // A private helper that runs a filter its callers build is as
                // scoped as they are: aware when it has callers in this type
                // and every one of them is.
                if (TakesFilter(m) && m is BaseMethodDeclarationSyntax method
                    && method.Modifiers.Any(SyntaxKind.PrivateKeyword))
                {
                    var name = MemberName(m);
                    var callers = members.Where(o => o != m && o.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(i => i.Identifier.Text == name)).ToList();
                    if (callers.Count > 0 && callers.All(aware.Contains))
                        changed |= aware.Add(m);
                }
            }
        } while (changed);

        return aware;
    }

    private static bool TakesFilter(SyntaxNode member) =>
        member is BaseMethodDeclarationSyntax m && m.ParameterList.Parameters.Any(p =>
            p.Type is GenericNameSyntax { Identifier.Text: "FilterDefinition" or "Expression" or "PipelineDefinition" });

    private static bool MentionsTenant(SyntaxNode node) =>
        node.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken)
            && t.ValueText.Contains("tenant", StringComparison.OrdinalIgnoreCase));

    private static SyntaxList<AttributeListSyntax> MemberAttributes(SyntaxNode member) => member switch
    {
        BaseMethodDeclarationSyntax m => m.AttributeLists,
        BasePropertyDeclarationSyntax p => p.AttributeLists,
        LocalFunctionStatementSyntax l => l.AttributeLists,
        AccessorDeclarationSyntax a => a.AttributeLists,
        _ => default,
    };

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Any(a =>
            SimpleName(a.Name) is AttributeName or AttributeName + "Attribute");

    private static string MemberName(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => ".ctor",
        PropertyDeclarationSyntax p => p.Identifier.Text,
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        BaseMethodDeclarationSyntax => "(operator)",
        BasePropertyDeclarationSyntax => "(indexer)",
        _ => member.Kind().ToString(),
    };

    private static string SimpleName(SyntaxNode type) => type switch
    {
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        NullableTypeSyntax n => SimpleName(n.ElementType),
        _ => type.ToString(),
    };
}
