using System.Text.Json;
using Cirq.Components.Nonlinear;
using Cirq.Components.Spice;

namespace Cirq.UI.Services;

/// <summary>One model somebody imported, kept as the card it came from.</summary>
/// <param name="Name">The model's name.</param>
/// <param name="Kind">Which device it is.</param>
/// <param name="Card">The original <c>.model</c> text, which is the source of truth.</param>
/// <param name="AddedUtc">When it was imported.</param>
public sealed record UserModel(string Name, SpiceDeviceKind Kind, string Card, DateTimeOffset AddedUtc);

/// <summary>
/// The models somebody has imported from SPICE cards, kept between runs.
/// <para>
/// What is stored is the <b>card</b>, not the converted model. Cards are the thing a manufacturer
/// publishes and a person can read; keeping them means the library stays legible, an import can be
/// re-examined, and if what a card maps onto here ever improves, every saved model gets the
/// benefit without anybody re-importing anything.
/// </para>
/// <para>
/// They are registered at startup, before any circuit is opened, because a saved circuit names its
/// model and looks it up by name. A circuit using an imported part opened on a machine without it
/// loads with a warning and the default model rather than failing — which is the existing
/// behaviour for any model that has gone missing, and the right one.
/// </para>
/// </summary>
public sealed class UserModelStore
{
    private readonly string _path;
    private readonly List<UserModel> _models = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public UserModelStore(string? path = null)
    {
        _path = path ?? DefaultPath();

        Reload();
        RegisterAll();
    }

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "CirqAvalonia",
            "models.json");

    public string Location => _path;

    /// <summary>What has been imported, newest first.</summary>
    public IReadOnlyList<UserModel> Models => _models;

    /// <summary>
    /// Reads every card in some text, registers what it understands, and remembers it.
    /// </summary>
    public (IReadOnlyList<ImportedModel> Imported, IReadOnlyList<string> Problems) Import(string text)
    {
        var parsed = SpiceModelReader.Parse(text);

        List<ImportedModel> imported = [];

        foreach (var card in parsed.Cards)
        {
            imported.Add(SpiceModelImport.Register(card));

            _models.RemoveAll(m => string.Equals(m.Name, card.Name, StringComparison.OrdinalIgnoreCase));
            _models.Insert(0, new UserModel(card.Name, card.Kind, CardTextOf(text, card), DateTimeOffset.UtcNow));
        }

        if (imported.Count > 0) Flush();

        return (imported, parsed.Problems);
    }

    /// <summary>Takes an imported model out of the library and out of the store.</summary>
    public bool Remove(string name)
    {
        var model = _models.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        if (model is null) return false;

        _models.Remove(model);

        switch (model.Kind)
        {
            case SpiceDeviceKind.Diode: DiodeModel.Unregister(model.Name); break;
            case SpiceDeviceKind.Npn or SpiceDeviceKind.Pnp: BjtModel.Unregister(model.Name); break;
            default: MosfetModel.Unregister(model.Name); break;
        }

        Flush();
        return true;
    }

    /// <summary>
    /// The one card out of a block that defines a given model, so importing three at once stores
    /// three cards rather than three copies of the whole block.
    /// </summary>
    private static string CardTextOf(string text, SpiceModelCard card)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(".model", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains(card.Name, StringComparison.OrdinalIgnoreCase))
            {
                return text.Trim();
            }
        }

        return text.Trim();
    }

    private void RegisterAll()
    {
        foreach (var model in _models)
        {
            foreach (var card in SpiceModelReader.Parse(model.Card).Cards)
            {
                if (!string.Equals(card.Name, model.Name, StringComparison.OrdinalIgnoreCase)) continue;

                SpiceModelImport.Register(card);
            }
        }
    }

    private void Reload()
    {
        _models.Clear();

        try
        {
            if (!File.Exists(_path)) return;

            var loaded = JsonSerializer.Deserialize<List<UserModel>>(File.ReadAllText(_path), Options);

            if (loaded is not null) _models.AddRange(loaded);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged store is an empty one rather than a crash at startup, and the file is left
            // alone so it can be looked at.
        }
    }

    private void Flush()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_models, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to write the store is not a reason to lose the import.
        }
    }
}
