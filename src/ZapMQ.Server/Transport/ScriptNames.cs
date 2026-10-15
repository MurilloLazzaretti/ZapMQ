using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The objects of the database a script names: those it needs to find there, and those it makes
/// itself. Read with the parser of T-SQL, never by running the script. Names in another database,
/// of the system, temporary tables, table variables and the names of common table expressions are
/// left out; a name without a schema is taken as of dbo.
/// </summary>
public static class ScriptNames
{
    public sealed record Found(IReadOnlyList<string> Needed, IReadOnlyList<string> Made, IReadOnlyList<string> Problems);

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };

    public static Found Read(string script)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(script);
        var tree = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
            return new Found([], [], [.. errors.Take(3).Select(error => $"linha {error.Line}: {error.Message}")]);
        var visitor = new Visitor();
        tree.Accept(visitor);
        var made = visitor.Made.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A name without a schema may be an alias given in the same statement, or a common table expression.
        var needed = visitor.Needed
            .Where(use => !made.Contains(use.Name) && !(use.Bare && (visitor.Aliases.Contains(use.Base) || visitor.Expressions.Contains(use.Base))))
            .Select(use => use.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return new Found(needed, [.. made.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)], []);
    }

    /// <summary>
    /// schema.name, or null for what is not an object of this database.
    /// </summary>
    private static string? Name(SchemaObjectName? name)
    {
        if (name?.BaseIdentifier?.Value is not { Length: > 0 } baseName)
            return null;
        if (name.DatabaseIdentifier is not null || name.ServerIdentifier is not null)
            return null;
        if (baseName.StartsWith('#') || baseName.StartsWith('@'))
            return null;
        var schema = name.SchemaIdentifier?.Value is { Length: > 0 } given ? given : "dbo";
        return SystemSchemas.Contains(schema) ? null : $"{schema}.{baseName}";
    }

    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "value", "query", "exist", "nodes", "modify", "ToString", "GetAncestor", "GetDescendant", "GetLevel", "IsDescendantOf", "GetReparentedValue" };

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<(string Name, string Base, bool Bare)> Needed { get; } = [];
        public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Made { get; } = [];
        public HashSet<string> Expressions { get; } = new(StringComparer.OrdinalIgnoreCase);

        private void Need(SchemaObjectName? name)
        {
            if (Name(name) is { } found)
                Needed.Add((found, name!.BaseIdentifier.Value, name.SchemaIdentifier is null));
        }

        public override void Visit(TableReferenceWithAlias node)
        {
            if (node.Alias?.Value is { Length: > 0 } alias)
                Aliases.Add(alias);
        }

        private void Make(SchemaObjectName? name)
        {
            if (Name(name) is { } found)
                Made.Add(found);
        }

        public override void Visit(CommonTableExpression node) => Expressions.Add(node.ExpressionName.Value);

        // What is read, written or called.
        public override void Visit(NamedTableReference node) => Need(node.SchemaObject);
        public override void Visit(SchemaObjectFunctionTableReference node) => Need(node.SchemaObject);
        public override void Visit(ProcedureReference node)
        {
            // Those of the system (sp_executesql, sp_rename...) are in every database.
            if (node.Name?.BaseIdentifier?.Value is { } name && (name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)))
                return;
            Need(node.Name);
        }
        public override void Visit(AlterTableStatement node) => Need(node.SchemaObjectName);
        public override void Visit(CreateIndexStatement node) => Need(node.OnName);
        public override void Visit(CreateTriggerStatement node) => Need(node.TriggerObject?.Name);
        public override void Visit(TruncateTableStatement node) => Need(node.TableName);

        public override void Visit(FunctionCall node)
        {
            // dbo.Function(...): a function of the database, not one of the language.
            // A column of XML or of a spatial type has methods called the same way: column.value(...).
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: 1 } parts }
                && !Methods.Contains(node.FunctionName.Value) && !node.FunctionName.Value.StartsWith("ST", StringComparison.Ordinal))
                Needed.Add(($"{parts[0].Value}.{node.FunctionName.Value}", node.FunctionName.Value, false));
        }

        // What the script makes, which it does not need to find.
        public override void Visit(CreateTableStatement node) => Make(node.SchemaObjectName);
        public override void Visit(CreateViewStatement node) => Make(node.SchemaObjectName);
        public override void Visit(CreateOrAlterViewStatement node) => Make(node.SchemaObjectName);
        public override void Visit(CreateProcedureStatement node) => Make(node.ProcedureReference?.Name);
        public override void Visit(CreateOrAlterProcedureStatement node) => Make(node.ProcedureReference?.Name);
        public override void Visit(CreateFunctionStatement node) => Make(node.Name);
        public override void Visit(CreateOrAlterFunctionStatement node) => Make(node.Name);
        public override void Visit(CreateTypeTableStatement node) => Make(node.Name);
        public override void Visit(CreateTypeUddtStatement node) => Make(node.Name);
        public override void Visit(CreateSynonymStatement node) => Make(node.Name);
        public override void Visit(CreateSequenceStatement node) => Make(node.Name);
        public override void Visit(SelectStatement node) => Make(node.Into);

        // Dropped only if it is there: nothing to need.
        public override void Visit(DropTableStatement node)
        {
            if (!node.IsIfExists)
                foreach (var name in node.Objects)
                    Need(name);
        }
    }
}
