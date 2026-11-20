using SiftDB.Abstractions;
using SiftDB.Engine;

namespace SiftDB.Providers.Postgres.Tests;

/// <summary>The query mini-map (design section 14, F5): its tables, the conditions joining them, warnings and suggested joins.</summary>
public class JoinMapperTests
{
    private static readonly PostgresProvider Pg = new();

    private static RelationInfo Table(string name, string[] columns, params ForeignKeyInfo[] fks) =>
        new(name, RelationKind.Table, columns.Select(c => new ColumnInfo(c, "integer", true, null)).ToList(), ["id"], fks, []);

    private static ForeignKeyInfo Fk(string name, string column, string table, string referenced = "id") =>
        new(name, [column], "public", table, [referenced]);

    private static readonly DatabaseSchema Schema = new([
        new SchemaInfo("public", [
            Table("customer", ["id", "name"]),
            Table("orders", ["id", "customer_id", "total"], Fk("orders_customer_fk", "customer_id", "customer")),
            Table("product", ["id", "title"]),
            Table("order_line", ["order_id", "product_id", "qty"], Fk("line_order_fk", "order_id", "orders"), Fk("line_product_fk", "product_id", "product")),
            Table("employee", ["id", "manager_id"], Fk("employee_manager_fk", "manager_id", "employee")),
            Table("big_orders", ["id", "customer_id"]) with { Kind = RelationKind.View },
            Table("sales", ["customer_id", "total"]) with { Kind = RelationKind.MaterializedView },
            Table("events", ["id"]) with { Kind = RelationKind.PartitionedTable },
        ]),
    ]);

    private static JoinMap? Map(string sql) =>
        JoinMapper.Map(Schema, StatementSplitter.For(Pg.Parser), Pg.Identifiers, sql, new SqlRange(0, sql.Length));

    private static JoinMap MapOf(string sql) => Map(sql) ?? throw new InvalidOperationException("no map");

    private static MapNode Node(JoinMap m, string label) => m.Nodes.Single(n => n.Label == label);

    private static string Apply(string sql, CodeAction a) => sql.Remove(a.Start, a.Length).Insert(a.Start, a.Text);

    /// <summary>The edges between real tables, as "from.col=to.col" (kind), sorted, for short assertions.</summary>
    private static string[] Joins(JoinMap m) => m.Edges.Where(e => e.Kind != MapEdgeKind.Suggested)
        .SelectMany(e => e.Conditions.Select(c => $"{m.Nodes[e.From].Label}.{c.FromColumn}={m.Nodes[e.To].Label}.{c.ToColumn} {e.Kind}"))
        .Order().ToArray();

    [Theory]
    [InlineData("select 1")]
    [InlineData("select * from customer")]
    [InlineData("select * from customer c where c.id = 1")]
    public void Under_two_tables_there_is_no_map(string sql) => Assert.Null(Map(sql));

    [Fact]
    public void A_join_is_an_edge_labelled_with_its_columns_and_where_it_is_written()
    {
        const string sql = "select * from orders o join customer c on c.id = o.customer_id";
        var m = MapOf(sql);
        Assert.Equal(["o", "c"], m.Nodes.Select(n => n.Label));
        Assert.Equal("public.orders", Node(m, "o").Table);
        Assert.Equal("orders o", sql.Substring(Node(m, "o").Start, Node(m, "o").Length));
        var e = Assert.Single(m.Edges);
        Assert.Equal(MapEdgeKind.Join, e.Kind);
        var c = Assert.Single(e.Conditions);
        Assert.Equal("c.id = o.customer_id", sql.Substring(c.Start, c.Length));
        Assert.Equal(["c.id=o.customer_id Join"], Joins(m));
        Assert.All(m.Nodes, n => Assert.Null(n.Warning));
        Assert.True(m.Parses);
    }

    [Theory]
    [InlineData("select * from orders o, customer c where o.customer_id = c.id and c.name = 'x'", "o.customer_id=c.id Where")]
    [InlineData("select * from orders, customer where customer_id = customer.id", "orders.customer_id=customer.id Where")] // a column alone, owned by one table
    [InlineData("select * from orders o join customer c on o.customer_id::bigint = c.id::bigint", "o.customer_id=c.id Join")]
    [InlineData("select * from orders o join customer c using (id)", "o.id=c.id Join")]
    [InlineData("select * from orders o natural join customer c", "o.id=c.id Join")]
    [InlineData("select * from public.orders join public.customer on customer.id = orders.customer_id", "customer.id=orders.customer_id Join")]
    public void Conditions_are_read_from_where_using_natural_casts_and_bare_columns(string sql, string expected)
    {
        var m = MapOf(sql);
        Assert.Equal([expected], Joins(m));
        Assert.All(m.Nodes, n => Assert.Null(n.Warning));
    }

    [Fact]
    public void Several_conditions_between_two_tables_are_one_edge()
    {
        var m = MapOf("select * from order_line a join order_line b on a.order_id = b.order_id and b.product_id = a.product_id");
        var e = Assert.Single(m.Edges, e => e.Kind == MapEdgeKind.Join);
        Assert.Equal([("order_id", "order_id"), ("product_id", "product_id")], e.Conditions.Select(c => (c.FromColumn, c.ToColumn)));
    }

    [Theory]
    [InlineData("select * from orders o, customer c")]
    [InlineData("select * from orders o join customer c on o.total > 0")]
    [InlineData("select * from orders o join customer c on c.id = c.id")]
    public void A_table_with_no_join_condition_is_warned(string sql)
    {
        var m = MapOf(sql);
        Assert.Null(Node(m, "o").Warning);
        Assert.Equal(JoinMapper.NoJoinWarning, Node(m, "c").Warning);
    }

    [Fact]
    public void Of_three_tables_the_one_left_out_is_warned()
    {
        var m = MapOf("select * from orders o join customer c on c.id = o.customer_id join product p on true join order_line l on l.qty = 1");
        Assert.Null(Node(m, "o").Warning);
        Assert.Null(Node(m, "c").Warning);
        Assert.Null(Node(m, "p").Warning); // ON true: a product asked for
        Assert.Equal(JoinMapper.NoJoinWarning, Node(m, "l").Warning);
    }

    [Theory]
    [InlineData("select * from orders o cross join customer c")]
    [InlineData("select * from orders o join customer c on true")]
    [InlineData("select * from orders o, generate_series(1, 3) g")]
    [InlineData("select * from orders o, lateral (select 1) x")]
    [InlineData("insert into customer (id, name) select o.id, 'x' from orders o")]
    public void Products_asked_for_on_purpose_are_not_warned(string sql) => Assert.All(MapOf(sql).Nodes, n => Assert.Null(n.Warning));

    [Fact]
    public void Each_box_says_what_kind_of_relation_it_is()
    {
        var m = MapOf("select * from big_orders b join sales s on s.customer_id = b.customer_id join customer c on c.id = b.customer_id join events e on e.id = c.id");
        Assert.Equal(MapNodeKind.View, Node(m, "b").Kind);
        Assert.Equal(MapNodeKind.MaterializedView, Node(m, "s").Kind);
        Assert.Equal(MapNodeKind.Table, Node(m, "c").Kind);
        Assert.Equal(MapNodeKind.Table, Node(m, "e").Kind); // partitioned: a table to the reader
    }

    [Fact]
    public void A_function_in_from_is_found_by_its_alias()
    {
        var m = MapOf("select * from orders o join generate_series(1, 3) g on g.g = o.id");
        Assert.Equal(MapNodeKind.Function, Node(m, "g").Kind);
        Assert.Single(m.Edges, e => e.Kind == MapEdgeKind.Join);
    }

    [Fact]
    public void An_equality_in_set_is_not_a_join()
    {
        var m = MapOf("update orders o set total = c.id from customer c");
        Assert.True(Node(m, "o").Target);
        Assert.Empty(Joins(m));
        Assert.Equal(JoinMapper.NoJoinWarning, Node(m, "c").Warning);
    }

    [Theory]
    [InlineData("update orders o set total = 0 from customer c where c.id = o.customer_id", "o")]
    [InlineData("delete from orders o using customer c where c.id = o.customer_id", "o")]
    [InlineData("merge into orders o using customer c on c.id = o.customer_id when matched then delete", "o")]
    public void The_table_a_statement_changes_is_marked_and_its_other_tables_are_found(string sql, string target)
    {
        var m = MapOf(sql);
        Assert.Equal([target], m.Nodes.Where(n => n.Target).Select(n => n.Label));
        Assert.Single(Joins(m));
        Assert.All(m.Nodes, n => Assert.Null(n.Warning));
    }

    [Fact]
    public void A_subquery_has_its_own_scope_and_its_tables_are_not_taken_for_unjoined_outer_ones()
    {
        var m = MapOf("select * from orders o where o.customer_id in (select c.id from customer c where c.name = 'x')");
        Assert.Equal(2, m.Scopes.Count);
        Assert.Equal("subquery", m.Scopes[1].Kind);
        Assert.Equal(1, Node(m, "c").Scope);
        Assert.All(m.Nodes, n => Assert.Null(n.Warning));
    }

    [Fact]
    public void A_correlated_subquery_joins_across_scopes()
    {
        var m = MapOf("select * from orders o where exists (select 1 from customer c where c.id = o.customer_id)");
        Assert.Equal(["c.id=o.customer_id Where"], Joins(m));
        Assert.NotEqual(Node(m, "c").Scope, Node(m, "o").Scope);
    }

    [Fact]
    public void A_cte_body_is_a_named_scope_and_its_name_a_node()
    {
        var m = MapOf("with big as (select * from orders where total > 100) select * from big b join customer c on c.id = b.customer_id");
        Assert.Contains(m.Scopes, s => s.Kind == "cte" && s.Name == "big");
        Assert.Equal(MapNodeKind.Cte, Node(m, "b").Kind);
        Assert.Equal(["b.customer_id=c.id Join"], Joins(m).Select(j => j.Replace("c.id=b.customer_id", "b.customer_id=c.id")).ToArray());
        Assert.All(m.Nodes, n => Assert.Null(n.Warning));
    }

    [Fact]
    public void A_derived_table_is_a_node_named_by_its_alias_and_its_scope_too()
    {
        var m = MapOf("select * from customer c join (select customer_id, sum(total) s from orders group by 1) t on t.customer_id = c.id");
        Assert.Equal(MapNodeKind.Derived, Node(m, "t").Kind);
        Assert.Contains(m.Scopes, s => s.Kind == "subquery" && s.Name == "t");
        Assert.Single(Joins(m));
    }

    [Fact]
    public void A_half_typed_statement_still_maps_but_does_not_parse()
    {
        var m = MapOf("select * from orders o join customer c on c.id = o.customer_id where");
        Assert.False(m.Parses);
        Assert.Single(Joins(m));
    }

    // ---- suggested joins ----------------------------------------------------------------------------------------

    [Fact]
    public void A_foreign_key_between_unjoined_tables_is_suggested_and_its_fix_writes_on()
    {
        const string sql = "select * from orders o join customer c where c.name = 'x'";
        var m = MapOf(sql);
        var s = Assert.Single(m.Edges, e => e.Kind == MapEdgeKind.Suggested);
        Assert.Equal(("o", "c"), (m.Nodes[s.From].Label, m.Nodes[s.To].Label));
        Assert.Equal(("customer_id", "id"), (s.Conditions[0].FromColumn, s.Conditions[0].ToColumn));
        Assert.Equal("select * from orders o join customer c on o.customer_id = c.id where c.name = 'x'", Apply(sql, s.Fix!));
        Assert.Equal("Join o to c on orders_customer_fk", s.Fix!.Title);
    }

    [Theory]
    [InlineData("select * from orders o join customer c on c.name = 'x'",
        "select * from orders o join customer c on c.name = 'x' and o.customer_id = c.id")]
    [InlineData("select * from orders o, customer c where c.name = 'x' order by 1",
        "select * from orders o, customer c where c.name = 'x' and o.customer_id = c.id order by 1")]
    [InlineData("select * from orders o, customer c order by 1",
        "select * from orders o, customer c where o.customer_id = c.id order by 1")]
    [InlineData("select * from orders, customer",
        "select * from orders, customer where orders.customer_id = customer.id")]
    public void The_fix_adds_to_the_condition_already_there_or_starts_one(string sql, string expected)
    {
        var s = Assert.Single(MapOf(sql).Edges, e => e.Kind == MapEdgeKind.Suggested);
        Assert.Equal(expected, Apply(sql, s.Fix!));
        Assert.Null(Pg.Parser.Validate(expected));
    }

    [Theory]
    [InlineData("select * from orders o join customer c on c.name = 'x' or c.id = 1")] // an AND would change what the OR means
    [InlineData("select * from orders o, customer c where c.name = 'x' or c.id = 1")]
    public void No_fix_where_an_and_would_change_the_meaning(string sql) =>
        Assert.Null(Assert.Single(MapOf(sql).Edges, e => e.Kind == MapEdgeKind.Suggested).Fix);

    [Fact]
    public void Tables_already_joined_get_no_suggestion_even_on_other_columns()
    {
        Assert.DoesNotContain(MapOf("select * from orders o join customer c on c.id = o.customer_id").Edges, e => e.Kind == MapEdgeKind.Suggested);
        Assert.DoesNotContain(MapOf("select * from orders o join customer c on c.name = o.total::text").Edges, e => e.Kind == MapEdgeKind.Suggested);
    }

    [Fact]
    public void A_self_reference_is_suggested_once()
    {
        const string sql = "select * from employee e join employee m on true";
        var s = MapOf(sql).Edges.Where(e => e.Kind == MapEdgeKind.Suggested).ToList();
        Assert.Empty(s); // ON true joins them: nothing to suggest

        var open = MapOf("select * from employee e, employee m");
        var one = Assert.Single(open.Edges, e => e.Kind == MapEdgeKind.Suggested);
        Assert.Equal(("e", "m"), (open.Nodes[one.From].Label, open.Nodes[one.To].Label));
        Assert.Equal("select * from employee e, employee m where e.manager_id = m.id", Apply("select * from employee e, employee m", one.Fix!));
    }

    [Fact]
    public void Suggestions_stay_inside_a_scope()
    {
        var m = MapOf("select * from orders o where o.total > (select count(*) from customer c)");
        Assert.DoesNotContain(m.Edges, e => e.Kind == MapEdgeKind.Suggested);
    }
}
