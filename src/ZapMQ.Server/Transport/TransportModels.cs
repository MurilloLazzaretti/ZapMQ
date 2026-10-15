namespace ZapMQ.Server.Transport;

/// <summary>
/// Something waiting, in the area of the environment, to go into a package. An object of the
/// database is pointed at: its script is taken when the package is closed.
/// </summary>
public sealed class AreaItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "database";

    /// <summary>
    /// Define (create the object, or alter it where it exists), Drop or Script.
    /// </summary>
    public string Action { get; set; } = "Define";
    public string? ObjectKind { get; set; }
    public string? Variety { get; set; }
    public string? Schema { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// What a script is for, in the words of whoever wrote it.
    /// </summary>
    public string? Title { get; set; }
    public string? Script { get; set; }

    /// <summary>
    /// The database, only where the environment has more than one.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// What the object was like when it came into the area.
    /// </summary>
    public string? Fingerprint { get; set; }
    public string AddedBy { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// For what is made of files: the version it says it is, and what it carries.
    /// </summary>
    public string? Version { get; set; }
    public List<FileReference> Files { get; set; } = [];
    public long Size { get; set; }
}

/// <summary>
/// One file of a published folder, by its path from the folder with forward slashes.
/// </summary>
public sealed class FileReference
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class ItemReference
{
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public string? Kind { get; set; }
    public string? Database { get; set; }
}

public sealed class PackageItem
{
    public int Number { get; set; }
    public string Kind { get; set; } = "database";
    public string Action { get; set; } = "Define";
    public string? ObjectKind { get; set; }
    public string? Variety { get; set; }
    public string? Schema { get; set; }
    public string? Name { get; set; }
    public string? Title { get; set; }
    public string? Database { get; set; }

    /// <summary>
    /// Of the script the item carries.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// What the object was like, where the package was made, before the changes it carries.
    /// </summary>
    public string? Base { get; set; }

    /// <summary>
    /// The file of the item inside the package, and what tells it arrived whole.
    /// </summary>
    public string? File { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    public List<ItemReference> Uses { get; set; } = [];

    /// <summary>
    /// For what is made of files: the version it says it is, and every file it carries.
    /// </summary>
    public string? Version { get; set; }
    public List<FileReference> Files { get; set; } = [];

    /// <summary>
    /// What the target is where the package was made, for an environment that does not have it
    /// yet to install it. Null when it could not be told, or is not of a kind that is installed.
    /// </summary>
    public InstallInfo? Install { get; set; }

    /// <summary>
    /// For a whole web application: what it is, mfe or monolith. It only goes to an application of
    /// the same kind: the other kind is another application.
    /// </summary>
    public string? Mode { get; set; }
}

/// <summary>
/// How an application is set up where the package was made: suggestions for whoever installs it
/// somewhere else, never values of that environment that would be wrong in another.
/// </summary>
public sealed class InstallInfo
{
    /// <summary>
    /// The program, as a path inside the folder of the application.
    /// </summary>
    public string? Executable { get; set; }
    /// <summary>
    /// Processes of a group, or instances of an API.
    /// </summary>
    public int Instances { get; set; } = 1;
    /// <summary>
    /// The ports the instances of an API answer on there: only to show which lines of the proxy are theirs.
    /// </summary>
    public List<int> Ports { get; set; } = [];
    public string? DisplayName { get; set; }
    public string? StartType { get; set; }
    /// <summary>
    /// The files of configuration, with what looks like a secret left blank.
    /// </summary>
    public List<SettingTemplate> Settings { get; set; } = [];
    /// <summary>
    /// The lines of the reverse proxy that send requests to it there, as found in its configuration.
    /// </summary>
    public string? Proxy { get; set; }
}

public sealed class SettingTemplate
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>
    /// The names of what was left blank, for whoever fills it in.
    /// </summary>
    public List<string> Blanked { get; set; } = [];
}

/// <summary>
/// What whoever approved said about an application this environment does not have yet.
/// </summary>
public sealed class InstallChoice
{
    public string Folder { get; set; } = "";
    public string? Executable { get; set; }
    public int Instances { get; set; } = 1;
    public int Port { get; set; }
    public string? SiteName { get; set; }
    public bool SharedFolder { get; set; }
    public string? DisplayName { get; set; }
    public string? StartType { get; set; }
    public List<SettingText> Settings { get; set; } = [];
}

public sealed class SettingText
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// What a package says about itself. It is written once, when the package is closed, and is
/// the same in every environment the package goes through.
/// </summary>
public sealed class PackageManifest
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Origin { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<PackageItem> Items { get; set; } = [];
}

public sealed class HistoryEntry
{
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public string What { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>
/// How applying one item went: applied, failed, or unknown when the answer never came.
/// </summary>
public sealed class ItemResult
{
    public int Number { get; set; }
    public string Status { get; set; } = "";
    /// <summary>
    /// The item made, here, an application that was not here before.
    /// </summary>
    public bool Installed { get; set; }
    public string? Did { get; set; }
    public string? Problem { get; set; }
    public int? Batch { get; set; }
    public int? Line { get; set; }
    public List<string> Messages { get; set; } = [];
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// What was there before, to put it back by hand if it comes to that.
    /// </summary>
    public string? Previous { get; set; }
    public string? PreviousFingerprint { get; set; }

    /// <summary>
    /// Where, on the machine, the folder that was replaced is kept as it was.
    /// </summary>
    public string? Backup { get; set; }

    /// <summary>
    /// How putting it back went, when somebody asked for that: reverting, reverted, failed, or
    /// unknown when the answer never came. Null while nobody asked.
    /// </summary>
    public string? Reverted { get; set; }
    public string? RevertProblem { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
    public List<string> RevertMessages { get; set; } = [];

    /// <summary>
    /// What the package had left there, kept before it was put back as it was.
    /// </summary>
    public string? RevertReplaced { get; set; }
    public string? RevertBackup { get; set; }
}

/// <summary>
/// A package as one environment knows it: what it is, and what happened to it here.
/// </summary>
public sealed class PackageRecord
{
    public PackageManifest Manifest { get; set; } = new();

    /// <summary>
    /// Closed (made here), Pending, Rejected, Approved, Applying, Applied, Partial or Failed;
    /// and, when what it did was put back, Reverting, Reverted or RevertedPartly.
    /// </summary>
    public string Status { get; set; } = "Closed";

    /// <summary>
    /// It came from somewhere else, to be applied here.
    /// </summary>
    public bool Received { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset? ApplyAt { get; set; }
    public string? ApprovedBy { get; set; }
    public string? RevertBy { get; set; }

    /// <summary>
    /// Whoever approved it, or asked for it to be put back, also asked for what does not stop
    /// in time to be ended by force.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Who said they know it went wrong. From then on it does not ask for anybody any more,
    /// until something else happens to it.
    /// </summary>
    public string? AcknowledgedBy { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
    public List<ItemResult> Results { get; set; } = [];

    /// <summary>
    /// The environments that said they received it from here. Once one did, the package is
    /// part of what happened there and is not deleted here any more.
    /// </summary>
    public List<Delivery> Deliveries { get; set; } = [];

    /// <summary>
    /// By item: how what this environment does not have yet is to be installed, as said when approving.
    /// </summary>
    public Dictionary<int, InstallChoice> Installs { get; set; } = [];
}

public sealed class Delivery
{
    public string To { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
}

/// <summary>
/// Why something asked of the transport was not done, in words for whoever asked.
/// </summary>
public sealed class TransportRefused(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}
