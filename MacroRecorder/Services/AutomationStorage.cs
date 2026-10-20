using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MacroRecorder.Models;

namespace MacroRecorder.Services;

/// <summary>
/// Persistência das automações em JSON PORTABLE: o arquivo "automations.json"
/// fica ao lado do .exe (ou da pasta do projeto durante o desenvolvimento).
/// Usa System.Text.Json com enum convertido para string legível.
/// </summary>
public static class AutomationStorage
{
    private const string FileName = "automations.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }, // "LeftClick" em vez de 3
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Caminho do arquivo JSON no diretório do aplicativo.</summary>
    public static string StorePath
    {
        get
        {
            // Em single-file publish, Assembly.Location é vazio; usamos o
            // diretório do processo para manter a portabilidade real.
            string baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, FileName);
        }
    }

    // ==================== CARREGAR ====================

    /// <summary>Carrega as automações do disco. Retorna lista vazia se não existir/arquivo corrompido.</summary>
    public static List<Automation> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new List<Automation>();

            string json = File.ReadAllText(StorePath);
            var store = JsonSerializer.Deserialize<AutomationStore>(json, JsonOptions);
            return store?.Automations ?? new List<Automation>();
        }
        catch (Exception ex)
        {
            // Nunca derrubar o app por JSON inválido: loga e recomeça vazio,
            // preservando o arquivo danificado para análise manual.
            Logger.Error($"Falha ao carregar '{StorePath}'. Backup criado como automations.corrupt.json.", ex);
            try
            {
                string corrupt = Path.Combine(Path.GetDirectoryName(StorePath)!, "automations.corrupt.json");
                File.Copy(StorePath, corrupt, overwrite: true);
            }
            catch { /* melhor esforço */ }
            return new List<Automation>();
        }
    }

    // ==================== SALVAR ====================

    /// <summary>Salva toda a coleção de forma atômica (escreve .tmp e renomeia).</summary>
    public static void Save(IEnumerable<Automation> automations)
    {
        var store = new AutomationStore { Automations = automations.ToList() };
        string json = JsonSerializer.Serialize(store, JsonOptions);

        string tmpPath = StorePath + ".tmp";

        // Escrita atômica: se o processo cair no meio, o original não é perdido.
        File.WriteAllText(tmpPath, json);

        if (File.Exists(StorePath))
            File.Replace(tmpPath, StorePath, null);
        else
            File.Move(tmpPath, StorePath);
    }

    /// <summary>Adiciona/atualiza uma automação específica e persiste.</summary>
    public static void Upsert(Automation automation, List<Automation> collection)
    {
        int idx = collection.FindIndex(a => a.Id == automation.Id);
        if (idx >= 0) collection[idx] = automation;
        else collection.Add(automation);

        Save(collection);
    }

    /// <summary>Remove uma automação pelo Id e persiste.</summary>
    public static bool Delete(string id, List<Automation> collection)
    {
        int removed = collection.RemoveAll(a => a.Id == id);
        if (removed > 0) Save(collection);
        return removed > 0;
    }
}
