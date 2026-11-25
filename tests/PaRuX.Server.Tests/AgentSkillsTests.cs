using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.AgentSkills;
using PaRuX.Server.ArtifactWorkspace;
using PaRuX.Server.DesktopIntegration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-agent-skills-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;
    private readonly AttachedDataSources _sources;
    private readonly ReadOnlySqlQueryService _sql;

    public AgentSkillsTests()
    {
        Directory.CreateDirectory(_root);
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_outside);
        _sources = new AttachedDataSources(Path.Combine(_root, "agent-data"));
        _sql = new ReadOnlySqlQueryService(_sources);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeSqlite(string name = "shop.sqlite", int rows = 5)
    {
        string path = Path.Combine(_outside, name);
        using (var c = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE sales (id INTEGER PRIMARY KEY, region TEXT, amount REAL);"
                + string.Join("", Enumerable.Range(1, rows).Select(i => $"INSERT INTO sales VALUES ({i}, '{(i % 2 == 0 ? "north" : "south")}', {i * 10});"));
            cmd.ExecuteNonQuery();
        }
        return _sources.Attach(path).Name;
    }

    private string MakeCsv(string name, string content)
    {
        string path = Path.Combine(_outside, name);
        File.WriteAllText(path, content);
        return _sources.Attach(path).Name;
    }

    // ---- statement guard ----

    [Theory]
    [InlineData("INSERT INTO sales VALUES (1,'a',1)")]
    [InlineData("UPDATE sales SET amount = 0")]
    [InlineData("DELETE FROM sales")]
    [InlineData("DROP TABLE sales")]
    [InlineData("SELECT 1; DROP TABLE sales")]
    [InlineData("SELECT 1 /* x */ ; /* y */ DELETE FROM sales")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM sales")]
    [InlineData("PRAGMA writable_schema = 1")]
    [InlineData("ATTACH DATABASE 'x.db' AS other")]
    [InlineData("VACUUM INTO 'copy.db'")]
    [InlineData("SELECT load_extension('evil')")]
    [InlineData("SELECT readfile('C:/Windows/win.ini')")]
    [InlineData("SELECT zeroblob(1000000000)")]
    [InlineData("-- hidden\nCREATE TABLE t(x)")]
    [InlineData("EXPLAIN")]
    [InlineData("EXPLAIN DELETE FROM sales")]
    [InlineData("EXPLAIN QUERY PLAN INSERT INTO sales VALUES (1,'a',1)")]
    [InlineData("EXPLAIN ATTACH DATABASE 'x.db' AS other")]
    [InlineData("REINDEX")]
    [InlineData("ANALYZE")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("")]
    [InlineData("   ")]
    public void Guard_refuses_anything_but_one_select(string sql) =>
        Assert.NotNull(SqlStatementGuard.Validate(sql));

    [Theory]
    [InlineData("SELECT * FROM sales")]
    [InlineData("select region, sum(amount) from sales group by region;")]
    [InlineData("WITH t AS (SELECT 1 AS n) SELECT n FROM t")]
    [InlineData("EXPLAIN SELECT * FROM sales")]
    [InlineData("EXPLAIN QUERY PLAN SELECT region, SUM(amount) FROM sales GROUP BY region")]
    [InlineData("EXPLAIN QUERY PLAN WITH t AS (SELECT 1 AS n) SELECT n FROM t")]
    [InlineData("SELECT 'DELETE FROM sales; DROP TABLE x' AS text")]
    [InlineData("SELECT CASE WHEN amount > 1 THEN 'big' ELSE 'small' END FROM sales -- DELETE")]
    [InlineData("SELECT replace(region, 'n', 'N') FROM sales")]
    [InlineData("SELECT \"update\" FROM sales")]
    public void Guard_accepts_plain_selects(string sql) =>
        Assert.Null(SqlStatementGuard.Validate(sql));

    [Fact]
    public void Default_sql_timeout_is_five_seconds_and_explain_query_plan_runs()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ReadOnlySqlQueryService.DefaultTimeout);
        string name = MakeSqlite();
        SqlQueryResult plan = _sql.Query(name, "EXPLAIN QUERY PLAN SELECT * FROM sales", 20, null, CancellationToken.None);
        Assert.NotEmpty(plan.Rows);
    }

    [Fact]
    public void Guard_refuses_unterminated_text_and_oversized_statements()
    {
        Assert.NotNull(SqlStatementGuard.Validate("SELECT 'abc"));
        Assert.NotNull(SqlStatementGuard.Validate("SELECT 1 /* never closed"));
        Assert.NotNull(SqlStatementGuard.Validate("SELECT " + new string('1', SqlStatementGuard.MaxStatementCharacters)));
    }

    // ---- path handling ----

    [Theory]
    [InlineData("..\\secret.sqlite")]
    [InlineData("../secret.sqlite")]
    [InlineData("sub/shop.sqlite")]
    [InlineData("C:\\Windows\\system32\\config.db")]
    [InlineData("/etc/passwd.db")]
    [InlineData("shop.sqlite:stream")]
    [InlineData("")]
    public void Source_names_that_are_paths_are_refused(string name) =>
        Assert.Throws<InvalidDataException>(() => _sources.Resolve(name, out _));

    [Fact]
    public void Only_supported_kinds_can_be_attached_and_queried()
    {
        string txt = Path.Combine(_outside, "notes.txt");
        File.WriteAllText(txt, "hello");
        Assert.Throws<InvalidDataException>(() => _sources.Attach(txt));
        Assert.Throws<InvalidDataException>(() => _sources.Attach("relative.csv"));
        Assert.Throws<InvalidDataException>(() => _sources.Resolve("notes.txt", out _));
    }

    [Fact]
    public void A_source_that_was_not_attached_is_not_reachable_even_with_a_known_file_next_to_it()
    {
        File.WriteAllText(Path.Combine(_outside, "private.csv"), "a,b\n1,2\n");
        Assert.Throws<InvalidDataException>(() => _sql.Query("private.csv", "SELECT * FROM data", 10, null, CancellationToken.None));
    }

    [Fact]
    public void A_file_that_is_not_sqlite_is_refused_even_with_a_database_extension()
    {
        string path = Path.Combine(_outside, "fake.db");
        File.WriteAllText(path, "this is not a database at all");
        string name = _sources.Attach(path).Name;
        Assert.Throws<InvalidDataException>(() => _sql.Query(name, "SELECT 1", 10, null, CancellationToken.None));
    }

    [Fact]
    public void Attach_never_overwrites_an_existing_name()
    {
        string first = MakeCsv("same.csv", "a\n1\n");
        string second = MakeCsv("same.csv", "a\n2\n");
        Assert.Equal("same.csv", first);
        Assert.Equal("same (2).csv", second);
    }

    // ---- queries ----

    [Fact]
    public void Select_returns_columns_and_rows()
    {
        string name = MakeSqlite();
        SqlQueryResult result = _sql.Query(name, "SELECT region, SUM(amount) AS total FROM sales GROUP BY region ORDER BY region", 50, null, CancellationToken.None);
        Assert.Equal(new[] { "region", "total" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("north", result.Rows[0][0]);
        Assert.Equal("60", result.Rows[0][1]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Write_attempts_are_refused_and_the_database_is_unchanged()
    {
        string name = MakeSqlite();
        string stored = Path.Combine(_sources.Root, name);
        byte[] before = File.ReadAllBytes(stored);
        foreach (string sql in new[] { "DELETE FROM sales", "INSERT INTO sales VALUES (99,'x',1)", "SELECT 1; DROP TABLE sales", "UPDATE sales SET amount = 0", "PRAGMA query_only = OFF" })
            Assert.Throws<InvalidDataException>(() => _sql.Query(name, sql, 10, null, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(stored));
        Assert.Equal("5", _sql.Query(name, "SELECT COUNT(*) FROM sales", 10, null, CancellationToken.None).Rows[0][0]);
    }

    [Fact]
    public void The_connection_itself_is_read_only_even_if_the_guard_were_bypassed()
    {
        string name = MakeSqlite();
        string path = Path.Combine(_sources.Root, name);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM sales";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Row_cap_truncates_and_says_so()
    {
        string name = MakeSqlite("big.sqlite", rows: 300);
        SqlQueryResult capped = _sql.Query(name, "SELECT * FROM sales", 25, null, CancellationToken.None);
        Assert.Equal(25, capped.Rows.Count);
        Assert.True(capped.Truncated);
        Assert.Contains("row cap", capped.TruncatedReason);
        SqlQueryResult hard = _sql.Query(name, "SELECT * FROM sales", 100000, null, CancellationToken.None);
        Assert.Equal(300, hard.Rows.Count);   // the hard cap is 1000, so all 300 fit
        Assert.False(hard.Truncated);
    }

    [Fact]
    public void Output_size_and_cell_length_are_bounded()
    {
        string name = MakeSqlite();
        SqlQueryResult wide = _sql.Query(name, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 900) SELECT i, printf('%.*c', 190, 'x') FROM n", 1000, null, CancellationToken.None);
        Assert.True(wide.Truncated);
        Assert.Contains("output size", wide.TruncatedReason);
        int characters = wide.Rows.Sum(r => r.Sum(c => c?.Length ?? 0));
        Assert.True(characters <= ReadOnlySqlQueryService.MaxOutputCharacters + 400);

        SqlQueryResult longCell = _sql.Query(name, "SELECT printf('%.*c', 5000, 'y')", 5, null, CancellationToken.None);
        Assert.True(longCell.Rows[0][0]!.Length <= ReadOnlySqlQueryService.MaxCellCharacters + 3);
    }

    [Fact]
    public void A_runaway_query_is_stopped_by_the_deadline()
    {
        string name = MakeSqlite();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => _sql.Query(name,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n) SELECT COUNT(*) FROM n",
            10, TimeSpan.FromMilliseconds(400), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Cancellation_interrupts_a_running_query()
    {
        string name = MakeSqlite();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => _sql.Query(name,
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n) SELECT COUNT(*) FROM n",
            10, TimeSpan.FromSeconds(30), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_cancelled_token_stops_before_any_work()
    {
        string name = MakeSqlite();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _sql.Query(name, "SELECT * FROM sales", 10, null, cts.Token));
    }

    [Fact]
    public void Csv_becomes_the_table_data_with_inferred_types_and_quoted_fields()
    {
        string name = MakeCsv("orders.csv", "Order ID,Customer Name,Total\n1,\"Smith, Anna\",10.5\n2,Bob,20\n3,\"He said \"\"hi\"\"\",4\n");
        SqlQueryResult rows = _sql.Query(name, "SELECT Customer_Name FROM data ORDER BY Order_ID", 10, null, CancellationToken.None);
        Assert.Equal(new[] { "Smith, Anna", "Bob", "He said \"hi\"" }, rows.Rows.Select(r => r[0]).ToArray());
        Assert.Equal("34.5", _sql.Query(name, "SELECT SUM(Total) FROM data", 10, null, CancellationToken.None).Rows[0][0]);
        Assert.Contains("Total REAL", _sql.DescribeSchema(name, CancellationToken.None));
    }

    [Fact]
    public void Csv_semicolon_and_tsv_delimiters_are_detected()
    {
        string semi = MakeCsv("semi.csv", "a;b\n1;2\n3;4\n");
        Assert.Equal("6", _sql.Query(semi, "SELECT SUM(b) FROM data", 10, null, CancellationToken.None).Rows[0][0]);
        string tsv = MakeCsv("t.tsv", "a\tb\n5\t6\n");
        Assert.Equal("11", _sql.Query(tsv, "SELECT a+b FROM data", 10, null, CancellationToken.None).Rows[0][0]);
    }

    [Fact]
    public void Csv_cannot_be_modified_through_sql()
    {
        string name = MakeCsv("ro.csv", "a\n1\n2\n");
        Assert.Throws<InvalidDataException>(() => _sql.Query(name, "DELETE FROM data", 10, null, CancellationToken.None));
        Assert.Equal("2", _sql.Query(name, "SELECT COUNT(*) FROM data", 10, null, CancellationToken.None).Rows[0][0]);
    }

    [Fact]
    public void Csv_with_too_many_columns_is_refused()
    {
        string name = MakeCsv("wide.csv", string.Join(",", Enumerable.Range(0, ReadOnlySqlQueryService.MaxCsvColumns + 1).Select(i => "c" + i)) + "\n");
        Assert.Throws<InvalidDataException>(() => _sql.Query(name, "SELECT 1", 10, null, CancellationToken.None));
    }

    [Fact]
    public void Schema_lists_tables_and_columns()
    {
        string name = MakeSqlite();
        string schema = _sql.DescribeSchema(name, CancellationToken.None);
        Assert.Contains("sales(", schema);
        Assert.Contains("region TEXT", schema);
    }

    // ---- charts ----

    private static ChartSpec SampleChart(string type = "bar") => new()
    {
        Type = type,
        Title = "Sales <by> region & \"year\"",
        Labels = { "North", "South", "East" },
        Series = { new ChartSeries { Name = "2025", Values = { 10, 20.5, 5 } }, new ChartSeries { Name = "2026", Values = { 12, 18, 9 } } }
    };

    [Fact]
    public void Chart_output_is_deterministic_and_matches_the_golden_hash()
    {
        string svg = ChartRenderer.RenderSvg(SampleChart());
        Assert.Equal(svg, ChartRenderer.RenderSvg(SampleChart()));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
        Assert.Equal("E5DD3C052D84D893E22C3671D784F329AE34FBE76425025D5748BCE31236DB3B", hash);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("bar")]
    [InlineData("scatter")]
    public void Chart_text_is_escaped_and_no_active_content_is_emitted(string type)
    {
        string svg = ChartRenderer.RenderSvg(SampleChart(type));
        Assert.DoesNotContain("<by>", svg);
        Assert.Contains("&lt;by&gt;", svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
        System.Xml.Linq.XDocument.Parse(svg);   // well-formed
    }

    [Fact]
    public void Scatter_renders_numeric_and_categorical_axes_deterministically()
    {
        var numeric = new ChartSpec
        {
            Type = "scatter",
            Title = "Latency vs Load",
            Labels = { "10", "25.5", "50" },
            Series = { new ChartSeries { Name = "p95", Values = { 12.5, 30.0, 88.2 } } }
        };
        string svg1 = ChartRenderer.RenderSvg(numeric);
        string svg2 = ChartRenderer.RenderSvg(numeric);
        Assert.Equal(svg1, svg2);
        Assert.Contains("<circle", svg1);
        Assert.DoesNotContain("<script", svg1, StringComparison.OrdinalIgnoreCase);
        System.Xml.Linq.XDocument.Parse(svg1);
    }

    [Fact]
    public void Pie_renders_and_is_well_formed()
    {
        var spec = new ChartSpec { Type = "pie", Title = "Share", Labels = { "a", "b" }, Series = { new ChartSeries { Name = "s", Values = { 3, 1 } } } };
        string svg = ChartRenderer.RenderSvg(spec);
        Assert.Contains("<path", svg);
        System.Xml.Linq.XDocument.Parse(svg);
    }

    [Fact]
    public void Chart_validation_rejects_bad_specs()
    {
        ChartSpec bad = SampleChart(); bad.Type = "radar";
        Assert.Throws<InvalidDataException>(() => ChartRenderer.RenderSvg(bad));
        ChartSpec mismatch = SampleChart(); mismatch.Series[0].Values.Add(1);
        Assert.Throws<InvalidDataException>(() => ChartRenderer.RenderSvg(mismatch));
        ChartSpec nan = SampleChart(); nan.Series[0].Values[0] = double.NaN;
        Assert.Throws<InvalidDataException>(() => ChartRenderer.RenderSvg(nan));
        ChartSpec many = SampleChart(); many.Labels = Enumerable.Range(0, ChartRenderer.MaxLabels + 1).Select(i => "l" + i).ToList();
        Assert.Throws<InvalidDataException>(() => ChartRenderer.RenderSvg(many));
        ChartSpec negPie = SampleChart("pie"); negPie.Series.RemoveAt(1); negPie.Series[0].Values[0] = -1;
        Assert.Throws<InvalidDataException>(() => ChartRenderer.RenderSvg(negPie));
    }

    // ---- tools ----

    private AgentSkillTools Tools(out DocumentWriteService documents)
    {
        string state = Path.Combine(_root, "state");
        documents = new DocumentWriteService(state);
        documents.SetOutputFolder(Directory.CreateDirectory(Path.Combine(_root, "docs")).FullName);
        return new AgentSkillTools(_sql, documents);
    }

    private static string Call(AgentSkillTools tools, string name, object args, CancellationToken ct = default) =>
        tools.CallAsync(name, JsonSerializer.Serialize(args), ct).GetAwaiter().GetResult();

    [Fact]
    public void Sql_tool_is_only_offered_when_data_is_attached_and_the_skill_allows_it()
    {
        AgentSkillTools tools = Tools(out _);
        Assert.DoesNotContain(tools.GetToolSchemas(null).Select(Name), n => n == AgentSkillTools.SqlToolName);
        MakeSqlite();
        Assert.Contains(tools.GetToolSchemas(null).Select(Name), n => n == AgentSkillTools.SqlToolName);
        Assert.DoesNotContain(tools.GetToolSchemas(new[] { "parux_make_chart" }).Select(Name), n => n == AgentSkillTools.SqlToolName);
        Assert.Empty(tools.GetToolSchemas(Array.Empty<string>()));
    }

    private static string Name(JsonNode? n) => n!["function"]!["name"]!.GetValue<string>();

    [Fact]
    public void Sql_tool_returns_json_and_honest_failures()
    {
        AgentSkillTools tools = Tools(out _);
        string name = MakeSqlite();
        string ok = Call(tools, AgentSkillTools.SqlToolName, new { source = name, sql = "SELECT COUNT(*) AS n FROM sales" });
        using JsonDocument doc = JsonDocument.Parse(ok);
        Assert.Equal("5", doc.RootElement.GetProperty("rows")[0][0].GetString());
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.SqlToolName, new { source = name, sql = "DROP TABLE sales" }));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.SqlToolName, new { source = "..\\x.sqlite", sql = "SELECT 1" }));
        Assert.Contains("sales(", Call(tools, AgentSkillTools.SqlToolName, new { source = name }));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, tools.CallAsync(AgentSkillTools.SqlToolName, "{not json", CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void Sql_tool_propagates_cancellation()
    {
        AgentSkillTools tools = Tools(out _);
        string name = MakeSqlite();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Call(tools, AgentSkillTools.SqlToolName, new { source = name, sql = "SELECT 1" }, cts.Token));
    }

    [Fact]
    public void Chart_tool_saves_an_svg_without_overwriting_and_refuses_path_names()
    {
        AgentSkillTools tools = Tools(out DocumentWriteService documents);
        object args = new { fileName = "sales", type = "bar", title = "T", labels = new[] { "a", "b" }, series = new[] { new { name = "s", values = new[] { 1.0, 2.0 } } } };
        string first = Call(tools, AgentSkillTools.ChartToolName, args);
        string second = Call(tools, AgentSkillTools.ChartToolName, args);
        Assert.Contains("Saved chart sales.svg", first);
        Assert.Contains("sales (2).svg", second);
        Assert.True(File.Exists(Path.Combine(documents.OutputFolder, "sales.svg")));
        string traversal = Call(tools, AgentSkillTools.ChartToolName, new { fileName = "..\\..\\evil", type = "bar", title = "T", labels = new[] { "a" }, series = new[] { new { name = "s", values = new[] { 1.0 } } } });
        Assert.StartsWith(AgentSkillTools.FailurePrefix, traversal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.svg")));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.ChartToolName, new { fileName = "x", type = "bar", labels = new[] { "a" }, series = new[] { new { name = "s", values = new[] { 1.0, 2.0 } } } }));
    }

    [Fact]
    public void Chart_tool_registers_saved_svg_in_artifact_store()
    {
        string state = Path.Combine(_root, "state-artifacts");
        var documents = new DocumentWriteService(state);
        documents.SetOutputFolder(Directory.CreateDirectory(Path.Combine(_root, "docs-artifacts")).FullName);
        var artifacts = new ArtifactStore(state);
        var tools = new AgentSkillTools(_sql, documents, artifacts);

        string reply = Call(tools, AgentSkillTools.ChartToolName, new
        {
            fileName = "scatter_demo",
            type = "scatter",
            title = "Scatter Demo",
            labels = new[] { "1", "2", "3" },
            series = new[] { new { name = "s", values = new[] { 2.0, 4.0, 6.0 } } }
        });
        Assert.Contains("Artifact Workspace", reply);
        IReadOnlyList<ArtifactDto> list = artifacts.List(artifactType: "chart");
        ArtifactDto created = Assert.Single(list);
        Assert.Equal("Scatter Demo", created.Title);
        Assert.Equal("skill:charts", created.Source);
        Assert.True(File.Exists(Path.Combine(documents.OutputFolder, "scatter_demo.svg")));
    }

    [Fact]
    public void Presentation_tool_writes_a_valid_pptx_with_the_requested_slides()
    {
        AgentSkillTools tools = Tools(out DocumentWriteService documents);
        string result = Call(tools, AgentSkillTools.PresentationToolName, new
        {
            fileName = "deck", title = "Deck",
            slides = new[] { new { title = "One", bullets = new[] { "a", "b" }, notes = "n" }, new { title = "Two", bullets = new[] { "c" }, notes = "" } }
        });
        Assert.Contains("2 slides", result);
        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(documents.OutputFolder, "deck.pptx"));
        Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)));
    }

    [Fact]
    public void Presentation_tool_is_bounded_and_refuses_path_names()
    {
        AgentSkillTools tools = Tools(out DocumentWriteService documents);
        var tooMany = Enumerable.Range(0, AgentSkillTools.MaxSlides + 1).Select(i => new { title = "s" + i, bullets = new[] { "x" }, notes = "" }).ToArray();
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.PresentationToolName, new { fileName = "big", slides = tooMany }));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.PresentationToolName, new { fileName = "..\\x", slides = new[] { new { title = "t", bullets = new[] { "b" }, notes = "" } } }));
        Assert.StartsWith(AgentSkillTools.FailurePrefix, Call(tools, AgentSkillTools.PresentationToolName, new { fileName = "none", slides = Array.Empty<object>() }));
        Assert.Empty(Directory.GetFiles(documents.OutputFolder));
    }

    // ---- manifests ----

    private static string SkillsDirectory()
    {
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && probe != null; i++)
        {
            string candidate = Path.Combine(probe, "skills", "built-in");
            if (Directory.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("skills/built-in");
    }

    [Theory]
    [InlineData("sql-read", "parux_sql_query")]
    [InlineData("charts", "parux_make_chart")]
    [InlineData("document-writer", "parux_write_presentation")]
    public void Skill_manifests_are_valid_least_privilege_and_use_only_known_tools(string id, string expectedTool)
    {
        string json = File.ReadAllText(Path.Combine(SkillsDirectory(), id + ".json"));
        SkillManifest manifest = JsonSerializer.Deserialize<SkillManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(id, manifest.Id);
        Assert.Equal("builtin", manifest.Source);
        Assert.Contains(expectedTool, manifest.AllowedTools);
        Assert.Equal("off", manifest.InternetPolicy);
        Assert.Empty(SkillPermissionPolicy.UnknownTools(manifest));
        Assert.False(SkillPermissionPolicy.IsQuarantined(manifest));
        SkillPermissionPolicy.Validate(manifest);
        SkillPermissions p = manifest.Permissions;
        Assert.False(p.ReadFiles || p.WriteFiles || p.DeleteFiles || p.Shell || p.Network || p.Browser || p.ComputerUse || p.ExternalProcesses);
        Assert.False(string.IsNullOrWhiteSpace(manifest.SlashCommand));
        Assert.True(manifest.TimeoutSeconds is > 0 and <= 300);
    }

    [Fact]
    public void Skill_slash_commands_do_not_collide_with_other_built_ins()
    {
        var commands = Directory.GetFiles(SkillsDirectory(), "*.json")
            .Select(f => JsonSerializer.Deserialize<SkillManifest>(File.ReadAllText(f), new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(m => !string.IsNullOrEmpty(m.SlashCommand)).Select(m => m.SlashCommand).ToList();
        Assert.Equal(commands.Count, commands.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
