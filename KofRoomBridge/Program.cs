using System.Net.Http.Json;
using System.Text.Json;

const string RequestPath = "save/koff-room-request.json";
const string ResponsePath = "save/koff-room-response.json";
Directory.CreateDirectory("save");

if (args.Contains("--watch", StringComparer.OrdinalIgnoreCase))
{
    using var mutex = new Mutex(true, @"Local\KofAndrewRoomBridge_" + Environment.CurrentDirectory.GetHashCode(), out var first);
    if (!first) return;
    while (true)
    {
        try
        {
            if (File.Exists(RequestPath) && !File.Exists(ResponsePath)) await ProcessRequestAsync();
            await Task.Delay(25);
        }
        catch { await Task.Delay(250); }
    }
}
else await ProcessRequestAsync();

static async Task ProcessRequestAsync()
{
    try
    {
        using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(RequestPath));
        var request = requestDocument.RootElement;
        var action = request.GetProperty("action").GetString() ?? "";
        var candidates = new[] { "http://127.0.0.1:5088", ReadConfiguredUrl() }.Distinct(StringComparer.OrdinalIgnoreCase);
        Exception? lastError = null;
        foreach (var baseUrl in candidates)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMilliseconds(1200) };
                using var response = action switch
                {
                    "rooms" => await http.GetAsync("api/rooms"),
                    "create" => await http.PostAsJsonAsync("api/rooms", new { roomName = Text(request, "roomName"), nickname = Text(request, "nickname"), spectatorCapacity = request.GetProperty("spectatorCapacity").GetInt32() }),
                    "join" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/join", new { nickname = Text(request, "nickname"), joinQueue = false }),
                    "snapshot" => await http.GetAsync($"api/rooms/{Text(request, "roomId")}"),
                    "heartbeat" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/heartbeat", new { playerId = Text(request, "playerId") }),
                    "leave" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/leave", new { playerId = Text(request, "playerId") }),
                    "queue" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/queue", new { playerId = Text(request, "playerId"), join = request.GetProperty("join").GetBoolean() }),
                    "start" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/start", new { playerId = Text(request, "playerId") }),
                    "chat" => await http.PostAsJsonAsync($"api/rooms/{Text(request, "roomId")}/chat", new { playerId = Text(request, "playerId"), message = Text(request, "message") }),
                    _ => throw new InvalidOperationException("Ação desconhecida.")
                };
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    await WriteAsync(new { ok = false, error = TryError(body) ?? $"Erro do servidor ({(int)response.StatusCode}).", serverUrl = baseUrl });
                    return;
                }
                using var data = JsonDocument.Parse(body);
                await WriteAsync(new { ok = true, serverUrl = baseUrl, data = data.RootElement });
                return;
            }
            catch (Exception ex) { lastError = ex; }
        }
        await WriteAsync(new { ok = false, error = "Servidor offline. Inicie o KOF Server e tente novamente.", detail = lastError?.Message });
    }
    catch (Exception ex) { await WriteAsync(new { ok = false, error = "Falha ao processar a sala.", detail = ex.Message }); }
}

static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";
static async Task WriteAsync(object value)
{
    var temporary = ResponsePath + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value));
    File.Move(temporary, ResponsePath, true);
}
static string? TryError(string json) { try { return JsonDocument.Parse(json).RootElement.GetProperty("error").GetString(); } catch { return null; } }
static string ReadConfiguredUrl()
{
    foreach (var path in new[] { "launcher-config.json", "../launcher-config.json" })
        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.GetProperty("ServerUrl").GetString() ?? "http://26.152.187.43:5088"; } catch { }
    return "http://26.152.187.43:5088";
}
