using ZapMQ.Server.Transport;

namespace ZapMQ.Server.Tests;

/// <summary>
/// The configuration of an application as it travels to be installed elsewhere: what looks like a
/// secret is left blank whatever the kind of file, and the lines of the proxy follow the ports
/// chosen where it is installed.
/// </summary>
public sealed class InstallTextTests
{
    [Fact]
    public void Secrets_are_left_blank_in_files_of_every_kind_and_the_rest_stays()
    {
        var (ini, iniBlanked) = InstallText.Blank("app.ini", "[db]\nserver=db01\nuser=app\npassword=s3cr3t\nsenha = outra\n");
        Assert.Equal("[db]\nserver=db01\nuser=app\npassword=\nsenha = \n", ini);
        Assert.Equal(["password", "senha"], iniBlanked);

        var (env, _) = InstallText.Blank(".env", "API_KEY=abc\nCLIENT_SECRET=def\nURL=https://x\n");
        Assert.Equal("API_KEY=\nCLIENT_SECRET=\nURL=https://x\n", env);

        var (yml, _) = InstallText.Blank("config.yml", "db:\n  host: db01\n  password: s3cr3t\n");
        Assert.Equal("db:\n  host: db01\n  password: \n", yml);

        // A password inside a connection string, in an attribute that does not say it holds one.
        var (config, configBlanked) = InstallText.Blank("web.config", """<add name="Main" connectionString="Server=db;Password=s3cr3t;Database=x" /><add key="Mode" value="dev" />""");
        Assert.Equal("""<add name="Main" connectionString="Server=db;Password=;Database=x" /><add key="Mode" value="dev" />""", config);
        Assert.Equal(["connectionString (senha)"], configBlanked);

        // Nothing to hide: the file goes as it is.
        var (plain, none) = InstallText.Blank("environment.js", "window.env = { api: '/api' };");
        Assert.Equal("window.env = { api: '/api' };", plain);
        Assert.Empty(none);
    }

    [Fact]
    public void The_lines_of_the_proxy_take_the_ports_chosen_here()
    {
        var lines = InstallText.ProxyLines([("conf/apis.conf", "upstream orders {\n  server 127.0.0.1:9024;\n  server 127.0.0.1:9025;\n}\n# not this: upstream other { server 127.0.0.1:9030; }\n")], [9024, 9025])!;
        Assert.Contains("upstream orders", lines);
        Assert.DoesNotContain("9030", lines);
        Assert.StartsWith("# conf/apis.conf", lines);

        var here = InstallText.Adapt(lines, [9024, 9025], [19024, 19025]);
        Assert.Contains("127.0.0.1:19024;", here);
        Assert.Contains("127.0.0.1:19025;", here);
        Assert.DoesNotContain(":9024;", here);
        // Ports that are swapped do not step on each other.
        Assert.Equal("server a:9025; server b:9026;", InstallText.Adapt("server a:9024; server b:9025;", [9024, 9025], [9025, 9026]));
        Assert.Null(InstallText.ProxyLines([("nginx.conf", "server { listen 80; }")], [9024]));
    }
}
