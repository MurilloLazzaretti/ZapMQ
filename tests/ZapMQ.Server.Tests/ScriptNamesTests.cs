using ZapMQ.Server.Transport;

namespace ZapMQ.Server.Tests;

public class ScriptNamesTests
{
    [Fact]
    public void What_a_script_needs_leaves_out_what_it_makes_aliases_and_what_is_not_of_the_database()
    {
        var found = ScriptNames.Read("""
            CREATE TABLE dbo.Nova (Id int);
            GO
            WITH recent AS (SELECT * FROM Orders WHERE At > GETDATE() - 1)
            INSERT INTO dbo.Nova (Id) SELECT r.Id FROM recent r JOIN sales.Customers c ON c.Id = r.Customer;
            UPDATE o SET o.Total = dbo.Total(o.Id) FROM dbo.Orders o;
            DELETE FROM #work; DECLARE @t TABLE (Id int); SELECT * FROM @t;
            SELECT * FROM Other.dbo.Far; SELECT * FROM sys.objects; SELECT x.c.value('.', 'int') FROM dbo.Xml x;
            EXEC sp_executesql N'select 1'; EXEC dbo.Recalculate @Id = 1;
            ALTER TABLE dbo.Items ADD Note varchar(10);
            DROP TABLE IF EXISTS dbo.Gone; DROP TABLE dbo.Old;
            SELECT * INTO dbo.Copy FROM dbo.Items;
            """);

        Assert.Empty(found.Problems);
        Assert.Equal(["dbo.Items", "dbo.Old", "dbo.Orders", "dbo.Recalculate", "dbo.Total", "dbo.Xml", "sales.Customers"], found.Needed);
        Assert.Equal(["dbo.Copy", "dbo.Nova"], found.Made);
    }

    [Fact]
    public void A_procedure_made_by_the_script_is_not_needed_and_one_altered_is()
    {
        var found = ScriptNames.Read("""
            CREATE OR ALTER PROCEDURE dbo.Novo AS SELECT * FROM dbo.Base;
            GO
            ALTER PROCEDURE dbo.Antigo AS EXEC dbo.Novo;
            """);

        Assert.Equal(["dbo.Antigo", "dbo.Base"], found.Needed);
        Assert.Equal(["dbo.Novo"], found.Made);
    }

    [Fact]
    public void A_script_that_cannot_be_read_says_where()
    {
        var found = ScriptNames.Read("SELEC * FROM dbo.X");
        Assert.NotEmpty(found.Problems);
        Assert.Contains("linha 1", found.Problems[0]);
    }
}
