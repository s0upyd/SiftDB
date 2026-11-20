using SiftDB.Abstractions;
using static SiftDB.Engine.ScopeAnalyzer;

namespace SiftDB.Engine;

/// <summary>What a box stands for, so the map can tell a table from a view at a glance. A partitioned table is a table.</summary>
public enum MapNodeKind { Table, View, MaterializedView, ForeignTable, Cte, Derived, Function, Unknown }

/// <summary>
/// <c>Join</c>: an equality in an ON clause, a USING list or a NATURAL join. <c>Where</c>: an equality in WHERE.
/// <c>Cross</c>: a CROSS JOIN or <c>ON true</c>, a product asked for on purpose. <c>Suggested</c>: a foreign key between two
/// tables the statement does not join, not written yet.
/// </summary>
public enum MapEdgeKind { Join, Where, Cross, Suggested }

/// <summary>
/// The query mini-map of one statement (design section 14, F5). <see cref="Parses"/>: the statement is valid as it stands;
/// while it is not, the map is read from what is there (the UI greys it, or keeps its last good one).
/// </summary>
public sealed record JoinMap(IReadOnlyList<MapScope> Scopes, IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges, bool Parses);

/// <summary>
/// The statement itself (id 0, <c>Parent</c> -1), a CTE's body or a subquery, each with its own tables. <c>Name</c>: a CTE's
/// name, or a derived table's alias.
/// </summary>
public sealed record MapScope(int Id, int Parent, string Kind, string? Name, int Start, int Length);

/// <summary>
/// One table reference, as written from its schema to its alias (<c>Start</c>, <c>Length</c>), so clicking its box selects
/// it in the editor. <c>Label</c>: how the statement refers to it (its alias, else its name). <c>Table</c>: the relation it
/// resolved to, <c>schema.name</c>, or null. <c>Target</c>: the table an UPDATE, DELETE, INSERT or MERGE changes.
/// <c>Warning</c>: nothing joins it to the other tables of its scope.
/// </summary>
public sealed record MapNode(int Id, int Scope, string Label, string? Table, MapNodeKind Kind, bool Target, int Start, int Length, string? Warning);

/// <summary>
/// One condition of an edge: <c>FromColumn</c> belongs to the edge's From node. Its range is the condition as written, to
/// highlight on hover; a suggested edge's conditions are not written yet (<c>Length</c> 0).
/// </summary>
public sealed record MapCondition(string FromColumn, string ToColumn, int Start, int Length);

/// <summary>A line between two nodes. <c>Fix</c>: for a suggested edge, the edit that writes its condition, when there is a safe place for it.</summary>
public sealed record MapEdge(int From, int To, MapEdgeKind Kind, IReadOnlyList<MapCondition> Conditions, CodeAction? Fix);

/// <summary>
/// The query mini-map (design section 14, F5): the tables of the statement the cursor is in, the conditions that join them,
/// the ones that are not joined to the rest (every row × every row), and foreign keys between them it does not use.
///
/// Read from tokens, like completion and the identifier resolver, so it is provider-neutral and keeps working while the
/// statement is half-typed. Tables come from <see cref="ScopeAnalyzer"/>; parentheses that start a SELECT (a CTE's body, a
/// subquery, a derived table) are scopes of their own, so a subquery's tables are never taken for unjoined tables of the
/// outer query. Joins are equalities between columns of two tables (<c>a.x = b.y</c>, a column written alone when only one
/// table of its scope has it, either side optionally cast with <c>::</c>) in an ON or WHERE clause, USING lists and
/// NATURAL joins. A condition inside a subquery that names an outer table (a correlated subquery) is an edge between
/// scopes; it does not count as joining tables of the same scope. Conditions through functions or other operators
/// (<c>lower(a.x) = lower(b.y)</c>, <c>a.x &lt; b.y</c>) are not read yet.
/// </summary>
public static class JoinMapper
{
    public const string NoJoinWarning = "No join condition: every row × every row";

    /// <summary>The clause keywords: a condition's clause decides whether its equality is a join (ON, WHERE) or not (SET).</summary>
    private static readonly HashSet<string> ClauseWords =
    [
        "select", "from", "join", "on", "using", "where", "group", "having", "order", "limit", "offset", "fetch", "set",
        "returning", "values", "window", "when", "then", "into", "update", "delete", "insert", "merge", "union", "intersect",
        "except", "for",
    ];

    /// <summary>Where a statement's FROM list or a condition ends: the next clause of the same scope.</summary>
    private static readonly HashSet<string> EndsList =
    [
        "join", "inner", "left", "right", "full", "cross", "natural", "on", "using", "where", "group", "having", "order",
        "limit", "offset", "fetch", "window", "returning", "union", "intersect", "except", "for", "when", "set",
    ];

    /// <summary>The map of the statement in <paramref name="statement"/>, or null when it has fewer than two tables.</summary>
    public static JoinMap? Map(DatabaseSchema schema, ISqlParser parser, IIdentifierPolicy policy, string text, SqlRange statement)
    {
        var st = parser.Tokenize(text)
            .Where(t => t.Start >= statement.Start && t.End <= statement.End && t.Kind != SqlTokenKind.Comment).ToList();
        if (st.Count == 0) return null;
        var m = new Mapper(schema, policy, st, text, statement);
        if (!m.Build()) return null;
        return new JoinMap(m.ScopesOut(), m.NodesOut(), m.EdgesOut(),
            parser.Validate(text.Substring(statement.Start, statement.Length)) is null);
    }

    private sealed class Mapper(DatabaseSchema schema, IIdentifierPolicy policy, List<SqlToken> st, string text, SqlRange statement)
    {
        private sealed class Scope(int id, int parent, string kind, string? name, int openTok)
        {
            public int Id = id, Parent = parent, OpenTok = openTok, CloseTok = -1;
            public string Kind = kind;
            public string? Name = name;
        }

        private sealed class Node
        {
            public int Id, Scope, FirstTok, LastTok;
            public required ScopeEntry Entry;
            public MapNodeKind Kind;
            public bool Target, InsertTarget, Lateral;
            /// <summary>What conditions qualify its columns with: its alias, else its name (a function's alias after its arguments).</summary>
            public Ident Qualifier;
            public string? Warning;
        }

        private sealed class Edge
        {
            public int From, To;
            public MapEdgeKind Kind;
            public readonly List<MapCondition> Conditions = [];
            public CodeAction? Fix;
        }

        private readonly List<Scope> _scopes = [];
        private int[] _scopeOf = [];
        private string?[] _clause = [];
        private readonly List<Node> _nodes = [];
        private readonly List<Edge> _edges = [];

        private bool Punct(int i, char c) => i >= 0 && i < st.Count && IsPunct(st[i], text, c);
        private bool Word(int i, string w) => i >= 0 && i < st.Count && IsKeywordWord(st[i], text, w);
        private string Lower(int i) => Text(st[i], text).ToLowerInvariant();
        private bool IsOp(int i, string op) => i >= 0 && i < st.Count && st[i].Kind is SqlTokenKind.Operator or SqlTokenKind.Punctuation && Text(st[i], text) == op;

        public bool Build()
        {
            FindScopes();
            FindClauses();
            FindNodes();
            if (_nodes.Count < 2) return false;
            FindEqualities();
            FindUsingNaturalCross();
            Warn();
            Suggest();
            return true;
        }

        // ---- scopes ------------------------------------------------------------------------------------------------

        /// <summary>Each token's scope. A parenthesis that starts SELECT, WITH or VALUES opens one; any other is transparent.</summary>
        private void FindScopes()
        {
            _scopeOf = new int[st.Count];
            _scopes.Add(new Scope(0, -1, "statement", null, -1));
            var stack = new Stack<(int Outer, int Opened)>();
            var current = 0;
            for (var i = 0; i < st.Count; i++)
            {
                _scopeOf[i] = current;
                if (Punct(i, '('))
                {
                    if (i + 1 < st.Count && st[i + 1].Kind == SqlTokenKind.Keyword && Lower(i + 1) is "select" or "with" or "values")
                    {
                        var cte = CteName(i);
                        var s = new Scope(_scopes.Count, current, cte is null ? "subquery" : "cte", cte, i);
                        _scopes.Add(s);
                        stack.Push((current, s.Id));
                        current = s.Id;
                    }
                    else stack.Push((current, -1));
                }
                else if (Punct(i, ')') && stack.Count > 0)
                {
                    var (outer, opened) = stack.Pop();
                    if (opened >= 0) _scopes[opened].CloseTok = i;
                    current = outer;
                    _scopeOf[i] = current;
                }
            }
        }

        /// <summary>The name of the CTE whose body opens at <paramref name="open"/>: <c>name [(cols)] AS [[NOT] MATERIALIZED] (</c>.</summary>
        private string? CteName(int open)
        {
            var j = open - 1;
            if (Word(j, "materialized")) { j--; if (Word(j, "not")) j--; }
            if (!Word(j, "as")) return null;
            j--;
            if (Punct(j, ')')) j = MatchingOpen(j) - 1;
            return j >= 0 && IsName(st[j]) ? Ident.From(st[j], text).Raw : null;
        }

        private int MatchingOpen(int close)
        {
            var depth = 0;
            for (var i = close; i >= 0; i--)
            {
                if (Punct(i, ')')) depth++;
                else if (Punct(i, '(') && --depth == 0) return i;
            }
            return 0;
        }

        private int MatchingClose(int open)
        {
            var depth = 0;
            for (var i = open; i < st.Count; i++)
            {
                if (Punct(i, '(')) depth++;
                else if (Punct(i, ')') && --depth == 0) return i;
            }
            return st.Count - 1;
        }

        /// <summary>Each token's clause in its own scope (the last clause keyword before it there).</summary>
        private void FindClauses()
        {
            _clause = new string?[st.Count];
            var current = new Dictionary<int, string>();
            for (var i = 0; i < st.Count; i++)
            {
                var s = _scopeOf[i];
                if (st[i].Kind == SqlTokenKind.Keyword && ClauseWords.Contains(Lower(i))) current[s] = Lower(i);
                _clause[i] = current.GetValueOrDefault(s);
            }
        }

        // ---- nodes -------------------------------------------------------------------------------------------------

        private void FindNodes()
        {
            var cteNames = _scopes.Where(s => s.Kind == "cte").Select(s => s.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Analyze(schema, policy, st, text).Entries)
            {
                var n = new Node { Entry = e, Qualifier = e.AliasId ?? e.NameId };
                if (e.NameTok < 0)
                {
                    // A derived table: its subquery is the scope closed just before its alias.
                    var close = Word(e.AliasTok - 1, "as") ? e.AliasTok - 2 : e.AliasTok - 1;
                    var sub = _scopes.FirstOrDefault(s => s.CloseTok == close && close >= 0);
                    n.Kind = MapNodeKind.Derived;
                    n.FirstTok = sub?.OpenTok ?? e.AliasTok;
                    n.LastTok = e.AliasTok;
                    if (sub is not null) sub.Name ??= e.AliasId?.Raw;
                }
                else
                {
                    n.FirstTok = e.SchemaTok >= 0 ? e.SchemaTok : e.NameTok;
                    n.LastTok = Math.Max(e.NameTok, e.AliasTok);
                    if (Punct(e.NameTok + 1, '('))
                    {
                        // A function in FROM, generate_series(1, 10) g: its alias follows its arguments.
                        n.Kind = MapNodeKind.Function;
                        var i = MatchingClose(e.NameTok + 1) + 1;
                        if (Word(i, "as")) i++;
                        n.LastTok = i - 1;
                        if (i < st.Count && st[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)
                        {
                            n.Qualifier = Ident.From(st[i], text);
                            n.LastTok = i;
                        }
                    }
                    else n.Kind = e.Rel is { } rel
                        ? rel.Kind switch
                        {
                            RelationKind.View => MapNodeKind.View,
                            RelationKind.MaterializedView => MapNodeKind.MaterializedView,
                            RelationKind.ForeignTable => MapNodeKind.ForeignTable,
                            _ => MapNodeKind.Table, // a partitioned table is a table to the reader
                        }
                        : e.SchemaId is null && cteNames.Contains(e.NameId.Name) ? MapNodeKind.Cte : MapNodeKind.Unknown;
                }
                n.Scope = _scopeOf[n.FirstTok];

                var before = n.FirstTok - 1;
                if (Word(before, "only")) before--;
                n.Lateral = Word(before, "lateral");
                if (Word(before, "update")) n.Target = true;
                else if (Word(before, "into")) { n.Target = true; n.InsertTarget = !Word(before - 1, "merge"); }
                else if (Word(before, "from") && Word(before - 1, "delete")) n.Target = true;
                _nodes.Add(n);
            }
            _nodes.Sort((a, b) => a.FirstTok.CompareTo(b.FirstTok));
            for (var i = 0; i < _nodes.Count; i++) _nodes[i].Id = i;
        }

        // ---- column references ---------------------------------------------------------------------------------

        private readonly record struct ColumnRef(Ident? Qualifier, Ident Column, int FirstTok, int LastTok);

        /// <summary>A column reference that starts at <paramref name="i"/>: <c>col</c>, <c>q.col</c> or <c>s.q.col</c>, not a call.</summary>
        private ColumnRef? RefAt(int i)
        {
            if (i < 0 || i >= st.Count || !IsName(st[i]) || Punct(i - 1, '.')) return null;
            var parts = new List<int> { i };
            while (Punct(parts[^1] + 1, '.') && parts[^1] + 2 < st.Count && IsName(st[parts[^1] + 2])) parts.Add(parts[^1] + 2);
            var last = parts[^1];
            if (parts.Count > 3 || Punct(last + 1, '(')) return null;
            // A word alone is a column only when it is not a keyword (true, null, current_date…).
            if (parts.Count == 1 && st[i].Kind == SqlTokenKind.Keyword) return null;
            var qualifier = parts.Count >= 2 ? Ident.From(st[parts[^2]], text) : (Ident?)null;
            return new ColumnRef(qualifier, Ident.From(st[last], text), i, last);
        }

        /// <summary>The operand that ends at <paramref name="end"/> (a cast after it is skipped), read back to its start.</summary>
        private ColumnRef? RefEndingAt(int end)
        {
            if (end >= 2 && IsOp(end - 1, "::") && IsName(st[end])) end -= 2;
            var start = end;
            while (start >= 2 && Punct(start - 1, '.') && IsName(st[start - 2])) start -= 2;
            return RefAt(start) is { } r && r.LastTok == end ? r : null;
        }

        /// <summary>The operand that starts at <paramref name="start"/>, and the last token it takes (a cast after it included).</summary>
        private ColumnRef? RefStartingAt(int start, out int last)
        {
            last = start;
            if (RefAt(start) is not { } r) return null;
            last = r.LastTok;
            if (IsOp(last + 1, "::") && last + 2 < st.Count && IsName(st[last + 2])) last += 2;
            return r;
        }

        private bool SameName(string stored, Ident written) =>
            stored == written.Meaning(policy) || stored == written.Name || string.Equals(stored, written.Name, StringComparison.OrdinalIgnoreCase);

        /// <summary>The table a column belongs to: its own scope first, then the scopes around it (a correlated subquery).</summary>
        private Node? Owner(ColumnRef r, int scope)
        {
            for (var s = scope; s >= 0; s = _scopes[s].Parent)
            {
                var here = _nodes.Where(n => n.Scope == s).ToList();
                if (r.Qualifier is { } q)
                {
                    var wanted = q.Meaning(policy);
                    var hit = here.FirstOrDefault(n => n.Qualifier.Meaning(policy) == wanted)
                              ?? here.FirstOrDefault(n => string.Equals(n.Qualifier.Name, q.Name, StringComparison.OrdinalIgnoreCase));
                    if (hit is not null) return hit;
                }
                else
                {
                    // A column written alone: only when the scope's tables are all known and exactly one of them has it.
                    if (here.Count == 0) continue;
                    if (here.Any(n => n.Entry.Rel is null)) return null;
                    var has = here.Where(n => n.Entry.Rel!.Columns.Any(c => SameName(c.Name, r.Column))).ToList();
                    if (has.Count == 1) return has[0];
                    if (has.Count > 1) return null;
                }
            }
            return null;
        }

        private string ColumnName(Node owner, Ident column) =>
            owner.Entry.Rel?.Columns.FirstOrDefault(c => SameName(c.Name, column))?.Name ?? column.Name;

        // ---- edges -------------------------------------------------------------------------------------------------

        private Edge EdgeFor(Node a, Node b, MapEdgeKind kind)
        {
            var e = _edges.FirstOrDefault(x => x.Kind == kind && ((x.From == a.Id && x.To == b.Id) || (x.From == b.Id && x.To == a.Id)));
            if (e is null) _edges.Add(e = new Edge { From = a.Id, To = b.Id, Kind = kind });
            return e;
        }

        private void AddCondition(Node a, string aColumn, Node b, string bColumn, MapEdgeKind kind, int start, int end)
        {
            if (a == b) return;
            var e = EdgeFor(a, b, kind);
            e.Conditions.Add(e.From == a.Id
                ? new MapCondition(aColumn, bColumn, start, end - start)
                : new MapCondition(bColumn, aColumn, start, end - start));
        }

        /// <summary><c>x = y</c> in an ON or WHERE clause, between columns of two different tables.</summary>
        private void FindEqualities()
        {
            for (var i = 1; i + 1 < st.Count; i++)
            {
                if (!IsOp(i, "=") || _clause[i] is not ("on" or "where")) continue;
                if (RefEndingAt(i - 1) is not { } left || RefStartingAt(i + 1, out var lastRight) is not { } right) continue;
                var scope = _scopeOf[i];
                if (Owner(left, scope) is not { } a || Owner(right, scope) is not { } b || a == b) continue;
                AddCondition(a, ColumnName(a, left.Column), b, ColumnName(b, right.Column),
                    _clause[i] == "on" ? MapEdgeKind.Join : MapEdgeKind.Where, st[left.FirstTok].Start, st[lastRight].End);
            }
        }

        /// <summary>The last table of the same scope written before token <paramref name="i"/>: the left side of a join there.</summary>
        private Node? NodeBefore(int i, int scope, Func<Node, bool>? where = null) =>
            _nodes.LastOrDefault(n => n.Scope == scope && n.LastTok < i && (where?.Invoke(n) ?? true));

        private Node? NodeAfter(int i, int scope) => _nodes.FirstOrDefault(n => n.Scope == scope && n.FirstTok > i);

        /// <summary><c>JOIN b USING (x, y)</c>, <c>NATURAL JOIN b</c>, <c>CROSS JOIN b</c> and <c>JOIN b ON true</c>.</summary>
        private void FindUsingNaturalCross()
        {
            for (var i = 0; i < st.Count; i++)
            {
                if (st[i].Kind != SqlTokenKind.Keyword) continue;
                var scope = _scopeOf[i];
                switch (Lower(i))
                {
                    case "using" when Punct(i + 1, '(') && NodeBefore(i, scope) is { } joined:
                    {
                        var close = MatchingClose(i + 1);
                        for (var c = i + 2; c < close; c++)
                        {
                            if (!IsName(st[c])) continue;
                            var col = Ident.From(st[c], text);
                            var left = NodeBefore(joined.FirstTok, scope, n => n.Entry.Rel?.Columns.Any(x => SameName(x.Name, col)) ?? false)
                                       ?? NodeBefore(joined.FirstTok, scope);
                            if (left is not null)
                                AddCondition(left, ColumnName(left, col), joined, ColumnName(joined, col), MapEdgeKind.Join, st[i].Start, st[close].End);
                        }
                        break;
                    }
                    case "natural" when NodeAfter(i, scope) is { } joined && NodeBefore(i, scope) is { } left:
                    {
                        var shared = (left.Entry.Rel?.Columns ?? []).Select(c => c.Name)
                            .Intersect((joined.Entry.Rel?.Columns ?? []).Select(c => c.Name)).ToList();
                        var edge = EdgeFor(left, joined, MapEdgeKind.Join);
                        foreach (var c in shared) edge.Conditions.Add(new MapCondition(c, c, st[i].Start, st[joined.LastTok].End - st[i].Start));
                        break;
                    }
                    case "cross" when Word(i + 1, "join") && NodeAfter(i, scope) is { } joined && NodeBefore(i, scope) is { } left:
                        EdgeFor(left, joined, MapEdgeKind.Cross).Conditions.Add(new MapCondition("", "", st[i].Start, st[i + 1].End - st[i].Start));
                        break;
                    case "on" when Word(i + 1, "true") && (i + 2 >= st.Count || EndsAt(i + 2)) && NodeBefore(i, scope) is { } joined
                                   && NodeBefore(joined.FirstTok, scope) is { } left:
                        EdgeFor(left, joined, MapEdgeKind.Cross).Conditions.Add(new MapCondition("", "", st[i].Start, st[i + 1].End - st[i].Start));
                        break;
                }
            }
        }

        /// <summary>Token <paramref name="i"/> ends the clause before it: a clause keyword, a closing parenthesis or a semicolon.</summary>
        private bool EndsAt(int i) =>
            i >= st.Count || Punct(i, ')') || Punct(i, ';') || (st[i].Kind == SqlTokenKind.Keyword && EndsList.Contains(Lower(i)));

        // ---- warnings ----------------------------------------------------------------------------------------------

        /// <summary>
        /// In each scope, the tables that nothing joins to the rest. The largest joined group (on a tie, the one with the first
        /// table) is the query; every table outside it is warned. A function, a LATERAL item and an INSERT's target are not
        /// expected to be joined.
        /// </summary>
        private void Warn()
        {
            foreach (var scope in _scopes)
            {
                var members = _nodes.Where(n => n.Scope == scope.Id && !n.InsertTarget && !n.Lateral && n.Kind != MapNodeKind.Function).ToList();
                if (members.Count < 2) continue;
                var group = members.ToDictionary(n => n.Id, n => n.Id);
                int Find(int x) { while (group[x] != x) x = group[x] = group[group[x]]; return x; }
                foreach (var e in _edges)
                    if (group.ContainsKey(e.From) && group.ContainsKey(e.To)) group[Find(e.From)] = Find(e.To);
                var groups = members.GroupBy(n => Find(n.Id)).ToList();
                if (groups.Count < 2) continue;
                var main = groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Min(n => n.FirstTok)).First();
                foreach (var g in groups.Where(g => g != main))
                    foreach (var n in g) n.Warning = NoJoinWarning;
            }
        }

        // ---- suggested joins ---------------------------------------------------------------------------------------

        /// <summary>A foreign key between two tables of the same scope that the statement does not join at all: a dashed edge.</summary>
        private void Suggest()
        {
            var joined = _edges.Select(e => (Math.Min(e.From, e.To), Math.Max(e.From, e.To))).ToHashSet();
            foreach (var a in _nodes)
                foreach (var b in _nodes)
                {
                    if (a.Id >= b.Id || a.Scope != b.Scope || a.Entry.Rel is null || b.Entry.Rel is null || joined.Contains((a.Id, b.Id))) continue;
                    // The holder of the key first. Between two references to one table (employee e, employee m), only the
                    // earlier one is taken as the holder, so the key is suggested once.
                    foreach (var (holder, target) in a.Entry.Rel == b.Entry.Rel ? [(a, b)] : new[] { (a, b), (b, a) })
                        foreach (var fk in holder.Entry.Rel!.ForeignKeys.Where(fk => References(fk, target)))
                        {
                            var pairs = fk.Columns.Zip(fk.ReferencedColumns).ToList();
                            if (pairs.Count == 0) continue;
                            var edge = new Edge { From = holder.Id, To = target.Id, Kind = MapEdgeKind.Suggested };
                            var later = a.LastTok > b.LastTok ? a : b;
                            foreach (var (c, r) in pairs) edge.Conditions.Add(new MapCondition(c, r, st[later.LastTok].End, 0));
                            var condition = string.Join(" and ", pairs.Select(p => $"{Qualify(holder)}.{policy.Quote(p.First)} = {Qualify(target)}.{policy.Quote(p.Second)}"));
                            edge.Fix = Insertion(later, condition, $"Join {holder.Qualifier.Raw} to {target.Qualifier.Raw} on {fk.Name}");
                            _edges.Add(edge);
                        }
                }
        }

        private static bool References(ForeignKeyInfo fk, Node target) =>
            fk.ReferencedRelation == target.Entry.Rel!.Name && fk.ReferencedSchema == target.Entry.RelSchema;

        /// <summary>How a condition qualifies a table's columns: its alias as written, else its name as the dialect writes it.</summary>
        private string Qualify(Node n) => n.Entry.AliasId is { } a ? a.Raw : policy.Quote(n.Entry.Rel!.Name);

        /// <summary>
        /// Where a suggested condition goes, for the later of its two tables: <c>ON …</c> after a JOIN that has none, <c>AND …</c>
        /// at the end of its ON clause, or for a table in a FROM list, at the end of WHERE (or a new WHERE after the list).
        /// Nothing when that clause has an OR at its top level (an AND there would change what it means) or a USING.
        /// </summary>
        private CodeAction? Insertion(Node later, string condition, string title)
        {
            var scope = later.Scope;
            var before = later.FirstTok - 1;
            while (before >= 0 && st[before].Kind == SqlTokenKind.Keyword && Lower(before) is "only" or "lateral") before--;
            if (Word(before, "join"))
            {
                var next = later.LastTok + 1;
                if (Word(next, "using")) return null;
                if (!Word(next, "on")) return Insert(st[later.LastTok].End, $" on {condition}", title);
                var end = ClauseEnd(next + 1, scope, out var hasOr);
                return hasOr ? null : Insert(st[end].End, $" and {condition}", title);
            }
            if (!(Punct(before, ',') || Word(before, "from") || Word(before, "using"))) return null;
            var where = Enumerable.Range(later.LastTok + 1, Math.Max(0, st.Count - later.LastTok - 1))
                .FirstOrDefault(i => _scopeOf[i] == scope && Word(i, "where"), -1);
            if (where >= 0)
            {
                var end = ClauseEnd(where + 1, scope, out var hasOr);
                return hasOr ? null : Insert(st[end].End, $" and {condition}", title);
            }
            // No WHERE yet: a new one after the whole FROM clause, joins and their conditions included.
            var listEnd = ClauseEnd(later.LastTok + 1, scope, out _, EndsFrom);
            return Insert(st[Math.Max(listEnd, later.LastTok)].End, $" where {condition}", title);
        }

        /// <summary>What ends a whole FROM clause (its joins and ON conditions are part of it).</summary>
        private static readonly HashSet<string> EndsFrom =
            ["where", "group", "having", "order", "limit", "offset", "fetch", "window", "returning", "union", "intersect", "except", "for"];

        /// <summary>What ends an ON or WHERE condition: the next join or clause.</summary>
        private static readonly HashSet<string> EndsCondition = [.. EndsList.Except(["on", "using"])];

        private static CodeAction Insert(int at, string text, string title) => new(at, 0, text, title);

        /// <summary>
        /// The last token of a clause that starts at <paramref name="from"/>: before the next of <paramref name="stops"/> (by
        /// default, what ends a condition) in the same scope, the end of the scope, or a semicolon. <paramref name="hasOr"/>: an
        /// OR at the clause's own level.
        /// </summary>
        private int ClauseEnd(int from, int scope, out bool hasOr, HashSet<string>? stops = null)
        {
            stops ??= EndsCondition;
            hasOr = false;
            var depth = 0;
            var last = from - 1;
            for (var i = from; i < st.Count; i++)
            {
                if (_scopeOf[i] != scope && depth == 0) break;
                if (Punct(i, '(')) depth++;
                else if (Punct(i, ')')) { if (depth == 0) break; depth--; }
                else if (depth == 0 && Punct(i, ';')) break;
                else if (depth == 0 && st[i].Kind == SqlTokenKind.Keyword)
                {
                    var w = Lower(i);
                    if (w == "or") hasOr = true;
                    if (stops.Contains(w)) break;
                }
                last = i;
            }
            return Math.Max(last, 0);
        }

        // ---- out ---------------------------------------------------------------------------------------------------

        public IReadOnlyList<MapScope> ScopesOut() => _scopes.Select(s =>
        {
            int start, end;
            if (s.Id == 0) { start = statement.Start; end = statement.End; }
            else { start = st[s.OpenTok].Start; end = s.CloseTok >= 0 ? st[s.CloseTok].End : statement.End; }
            return new MapScope(s.Id, s.Parent, s.Kind, s.Name, start, end - start);
        }).ToList();

        public IReadOnlyList<MapNode> NodesOut() => _nodes.Select(n => new MapNode(
            n.Id, n.Scope, n.Qualifier.Raw, n.Entry.Rel is { } rel ? $"{n.Entry.RelSchema}.{rel.Name}" : null, n.Kind, n.Target,
            st[n.FirstTok].Start, st[n.LastTok].End - st[n.FirstTok].Start, n.Warning)).ToList();

        public IReadOnlyList<MapEdge> EdgesOut() => _edges.Select(e => new MapEdge(e.From, e.To, e.Kind, e.Conditions, e.Fix)).ToList();
    }
}
