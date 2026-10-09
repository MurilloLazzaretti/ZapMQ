using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZapMQ.Server.Transport;

/// <summary>
/// The area and the packages of this environment, kept as files under one folder:
/// <c>area.json</c>, and for each package its file as it travels (<c>package.zpkg</c>) beside
/// what happened to it here (<c>state.json</c>). A package file is never changed once written.
/// </summary>
public sealed class TransportStore
{
    public const string PackageFile = "package.zpkg";
    public const string ManifestEntry = "package.json";
    private const string StateFile = "state.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly string _root;
    private readonly ILogger _logger;
    private readonly List<AreaItem> _area = [];
    private readonly Dictionary<string, PackageRecord> _packages = new(StringComparer.OrdinalIgnoreCase);

    public TransportStore(TransportOptions options, ILogger<TransportStore> logger)
    {
        _logger = logger;
        _root = Path.GetFullPath(options.Directory, AppContext.BaseDirectory);
        Environment = string.IsNullOrWhiteSpace(options.Environment) ? System.Environment.MachineName : options.Environment.Trim();
        Load();
    }

    /// <summary>
    /// How this environment is called in what it writes.
    /// </summary>
    public string Environment { get; }

    private string AreaPath => Path.Combine(_root, "area.json");

    /// <summary>
    /// The files of an item of the area that is made of them, packed.
    /// </summary>
    public string AreaFile(string id) => Path.Combine(_root, "area-files", id + ".zip");

    /// <summary>
    /// Where something is put for the Worker Control, on the same machine, to pick up.
    /// </summary>
    public string WorkFile(string name) => Path.Combine(_root, "work", name);

    private string Folder(string id) => Path.Combine(_root, "packages", id);

    public string FilePath(string id) => Path.Combine(Folder(id), PackageFile);

    private void Load()
    {
        try
        {
            if (File.Exists(AreaPath))
                _area.AddRange(JsonSerializer.Deserialize<List<AreaItem>>(File.ReadAllText(AreaPath), Format) ?? []);
            var packages = Path.Combine(_root, "packages");
            if (!Directory.Exists(packages))
                return;
            foreach (var state in Directory.EnumerateFiles(packages, StateFile, SearchOption.AllDirectories))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PackageRecord>(File.ReadAllText(state), Format) is { Manifest.Id.Length: > 0 } record)
                        _packages[record.Manifest.Id] = record;
                }
                catch (JsonException error)
                {
                    _logger.LogError("The package in {File} could not be read and is being ignored: {Error}", state, error.Message);
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError("What the transport keeps in {Folder} could not be read: {Error}", _root, error.Message);
        }
    }

    private static void WriteAside(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    private void SaveArea() => WriteAside(AreaPath, JsonSerializer.Serialize(_area, Format));

    private void Save(PackageRecord record) => WriteAside(Path.Combine(Folder(record.Manifest.Id), StateFile), JsonSerializer.Serialize(record, Format));

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Format), Format)!;

    // ---------------------------------------------------------------- the area

    public List<AreaItem> Area()
    {
        lock (_gate)
            return Copy(_area);
    }

    /// <summary>
    /// Puts something in the area. An object that is already there is not put twice.
    /// </summary>
    public AreaItem Add(AreaItem item, byte[]? files = null)
    {
        lock (_gate)
        {
            if (item.Action != "Script" && _area.FirstOrDefault(other => Same(other, item)) is { } already)
            {
                // Asked to be dropped after having been asked to be carried, or the other way round: the last word stands.
                already.Action = item.Action;
                already.Fingerprint = item.Fingerprint;
                already.Version = item.Version;
                already.Files = item.Files;
                already.Size = item.Size;
                already.AddedBy = item.AddedBy;
                already.AddedAt = item.AddedAt;
                if (files is not null)
                    WriteBytes(AreaFile(already.Id), files);
                SaveArea();
                return Copy(already);
            }
            item.Id = Guid.NewGuid().ToString("N")[..12];
            if (files is not null)
                WriteBytes(AreaFile(item.Id), files);
            _area.Add(item);
            SaveArea();
            return Copy(item);
        }
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static bool Same(AreaItem one, AreaItem other) =>
        one.Action != "Script" && other.Action != "Script" && one.Kind == other.Kind && one.ObjectKind == other.ObjectKind
        && string.Equals(one.Schema, other.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(one.Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(one.Database, other.Database, StringComparison.OrdinalIgnoreCase);

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_area.RemoveAll(item => item.Id == id) == 0)
                return false;
            if (File.Exists(AreaFile(id)))
                File.Delete(AreaFile(id));
            SaveArea();
            return true;
        }
    }

    // ---------------------------------------------------------------- packages

    public List<PackageRecord> Packages()
    {
        lock (_gate)
            return [.. _packages.Values.Select(Copy).OrderByDescending(record => record.History.FirstOrDefault()?.At ?? record.Manifest.CreatedAt)];
    }

    public PackageRecord? Find(string id)
    {
        lock (_gate)
            return _packages.TryGetValue(id, out var record) ? Copy(record) : null;
    }

    /// <summary>
    /// Closes a package: writes its file from the items and their scripts, and takes out of the
    /// area what went into it.
    /// </summary>
    public PackageRecord Close(PackageManifest manifest, IReadOnlyList<byte[]> contents, IEnumerable<string> areaIds, string by, DateTimeOffset now)
    {
        var bytes = Write(manifest, contents);
        lock (_gate)
        {
            var record = new PackageRecord
            {
                Manifest = manifest, Status = "Closed", Sha256 = Hash(bytes), Size = bytes.Length,
                History = [new HistoryEntry { At = now, By = by, What = "closed", Detail = $"{manifest.Items.Count} item(s)" }]
            };
            Directory.CreateDirectory(Folder(manifest.Id));
            File.WriteAllBytes(FilePath(manifest.Id), bytes);
            _packages[manifest.Id] = record;
            Save(record);
            var gone = areaIds.ToHashSet();
            _area.RemoveAll(item => gone.Contains(item.Id));
            foreach (var id in gone.Where(id => File.Exists(AreaFile(id))))
                File.Delete(AreaFile(id));
            SaveArea();
            return Copy(record);
        }
    }

    private static byte[] Write(PackageManifest manifest, IReadOnlyList<byte[]> contents)
    {
        // The files first, so that the manifest can say what each one is.
        var files = new List<(string Name, byte[] Bytes)>();
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var item = manifest.Items[index];
            var content = contents[index];
            item.File = $"items/{item.Number:00}/{(item.Kind == "database" ? "script.sql" : "files.zip")}";
            item.Sha256 = Hash(content);
            item.Size = content.Length;
            files.Add((item.File, content));
        }

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, ManifestEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Format)));
            foreach (var (name, content) in files)
                Put(zip, name, content);
        }
        return memory.ToArray();

        static void Put(ZipArchive zip, string name, byte[] content)
        {
            // What is already packed is not packed again.
            var entry = zip.CreateEntry(name, name.EndsWith(".zip", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Takes in a package that was made somewhere else. It has to be whole, and one that is
    /// already here is not taken twice.
    /// </summary>
    public PackageRecord Import(byte[] bytes, string by, DateTimeOffset now)
    {
        PackageManifest manifest;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry(ManifestEntry) ?? throw new TransportRefused("O arquivo não é um pacote: falta o manifesto");
            using (var stream = entry.Open())
                manifest = JsonSerializer.Deserialize<PackageManifest>(stream, Format) ?? throw new TransportRefused("O manifesto do pacote está vazio");
            if (manifest.Format != 1)
                throw new TransportRefused($"O pacote é de um formato ({manifest.Format}) que esta versão não conhece");
            if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Any(c => !(char.IsLetterOrDigit(c) || c == '-')) || manifest.Items.Count == 0)
                throw new TransportRefused("O manifesto do pacote não tem identidade ou não tem itens");
            foreach (var item in manifest.Items)
            {
                var file = item.File is null ? null : zip.GetEntry(item.File);
                if (file is null)
                    throw new TransportRefused($"Falta no pacote o conteúdo do item {item.Number}");
                using var content = new MemoryStream();
                using (var stream = file.Open())
                    stream.CopyTo(content);
                if (!string.Equals(Hash(content.ToArray()), item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TransportRefused($"O conteúdo do item {item.Number} não é o que o pacote diz: o arquivo foi alterado ou chegou incompleto");
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new TransportRefused("O arquivo não é um pacote que se possa ler");
        }

        lock (_gate)
        {
            if (_packages.TryGetValue(manifest.Id, out var known))
                throw new TransportRefused($"Este pacote já está neste ambiente desde {known.History.FirstOrDefault()?.At.ToLocalTime():dd/MM/yyyy HH:mm}", StatusCodes.Status409Conflict);
            var record = new PackageRecord
            {
                Manifest = manifest, Status = "Pending", Received = true, Sha256 = Hash(bytes), Size = bytes.Length,
                History = [new HistoryEntry { At = now, By = by, What = "received", Detail = $"de {manifest.Origin}" }]
            };
            Directory.CreateDirectory(Folder(manifest.Id));
            File.WriteAllBytes(FilePath(manifest.Id), bytes);
            _packages[manifest.Id] = record;
            Save(record);
            return Copy(record);
        }
    }

    /// <summary>
    /// Removes a package that was made here, file and all, and puts back in the area what it
    /// carried. One that arrived from somewhere else is never removed: it can be refused, and
    /// what it brought stays to be looked at. Neither is one that another environment received.
    /// </summary>
    public PackageRecord Delete(string id, string by, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_packages.TryGetValue(id, out var record))
                throw new TransportRefused("Não existe esse pacote neste ambiente", StatusCodes.Status404NotFound);
            if (record.Received)
                throw new TransportRefused("Um pacote que chegou de outro ambiente não é excluído: ele pode ser recusado, e o que trouxe continua registrado", StatusCodes.Status409Conflict);
            if (record.Status != "Closed")
                throw new TransportRefused("Este pacote já foi aplicado neste ambiente, ou está para ser, e fica como registro do que foi feito", StatusCodes.Status409Conflict);
            if (record.Deliveries.Count > 0)
                throw new TransportRefused($"Este pacote já foi entregue a {string.Join(", ", record.Deliveries.Select(delivery => delivery.To).Distinct())} e não pode mais ser excluído aqui", StatusCodes.Status409Conflict);

            // Back to the area, as it was before the package was closed. An object is pointed at again; a script comes back with its text.
            foreach (var item in record.Manifest.Items)
            {
                var back = new AreaItem
                {
                    Id = Guid.NewGuid().ToString("N")[..12], Kind = item.Kind, Action = item.Action, ObjectKind = item.ObjectKind, Variety = item.Variety, Schema = item.Schema, Name = item.Name,
                    Title = item.Title, Database = item.Database, Fingerprint = item.Fingerprint, AddedBy = by, AddedAt = now,
                    Script = item.Action == "Script" ? Script(id, item.Number) : null, Version = item.Version, Files = item.Files, Size = item.Size
                };
                if (back.Action == "Script" ? back.Script is null : _area.Any(other => Same(other, back)))
                    continue;
                // What is made of files comes back with them.
                if (item.Kind != "database")
                {
                    if (Content(id, item.Number) is not { } files)
                        continue;
                    WriteBytes(AreaFile(back.Id), files);
                }
                _area.Add(back);
            }
            SaveArea();

            _packages.Remove(id);
            try
            {
                if (Directory.Exists(Folder(id)))
                    Directory.Delete(Folder(id), recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("The files of package {Id} could not be removed: {Error}", id, error.Message);
            }
            return record;
        }
    }

    /// <summary>
    /// The script an item carries, read from the file of the package.
    /// </summary>
    public string? Script(string id, int number) => Content(id, number) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>
    /// What an item carries, as it is in the file of the package: a script, or the files packed.
    /// </summary>
    public byte[]? Content(string id, int number)
    {
        PackageItem? item;
        lock (_gate)
            item = _packages.TryGetValue(id, out var record) ? record.Manifest.Items.FirstOrDefault(candidate => candidate.Number == number) : null;
        if (item?.File is null || !File.Exists(FilePath(id)))
            return null;
        using var zip = ZipFile.OpenRead(FilePath(id));
        if (zip.GetEntry(item.File) is not { } entry)
            return null;
        using var memory = new MemoryStream();
        using (var stream = entry.Open())
            stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Changes what is known of a package here, and writes it down. Null when there is no such package.
    /// </summary>
    public PackageRecord? Update(string id, Action<PackageRecord> change)
    {
        lock (_gate)
        {
            if (!_packages.TryGetValue(id, out var record))
                return null;
            change(record);
            Save(record);
            return Copy(record);
        }
    }
}
