
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

const string comfyUrl = "http://127.0.0.1:8188";
const string workflowPath = "workflow.json";
const string outputFolder = "output";



const string positivePrompt =
    "Cristiano Ronaldo, famous Portuguese football player, " +
    "wearing a red Portugal national team football jersey, " +
    "celebrating a goal with arms wide open, " +
    "standing on a football stadium pitch, " +
    "crowd cheering in the background, " +
    "dramatic stadium lighting, " +
    "realistic sports photography, " +
    "detailed face, athletic body, " +
    "sharp focus, cinematic composition, high quality";

const string negativePrompt =
    "blurry, low quality, distorted face, deformed body, " +
    "bad anatomy, extra fingers, extra arms, " +
    "duplicate person, unrealistic proportions, " +
    "cartoon, anime, painting, watermark, text, artifacts";



using var http = new HttpClient
{
    BaseAddress = new Uri(comfyUrl),
    Timeout = TimeSpan.FromMinutes(10)
};

var stopwatch = Stopwatch.StartNew();

try
{
    Console.WriteLine("=== ComfyUI .NET 10 POC ===");

    // 1. Đọc workflow API JSON.
    var json = await File.ReadAllTextAsync(workflowPath);

    var workflow = JsonNode.Parse(json)
        ?? throw new Exception("Workflow JSON is empty.");

    // Node 3: Positive Prompt.
    workflow["3"]!["inputs"]!["text"] = positivePrompt;

    // Node 4: Negative Prompt.
    workflow["4"]!["inputs"]!["text"] = negativePrompt;

    // Node 2: KSampler.
    workflow["2"]!["inputs"]!["seed"] = 42;
    workflow["2"]!["inputs"]!["steps"] = 10;

    // Node 5: Empty Latent Image.
    workflow["5"]!["inputs"]!["width"] = 512;
    workflow["5"]!["inputs"]!["height"] = 512;

    // Đặt prefix riêng cho ảnh được tạo qua API.
    workflow["7"]!["inputs"]!["filename_prefix"] =
        "DotNet_POC";

    // 2. Gửi workflow đến ComfyUI.
    Console.WriteLine("Submitting workflow...");

    var request = new JsonObject
    {
        ["prompt"] = workflow.DeepClone(),
        ["client_id"] = Guid.NewGuid().ToString()
    };

    using var response = await http.PostAsJsonAsync(
        "/prompt",
        request
    );

    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"ComfyUI rejected workflow: {responseBody}"
        );
    }

    var submission = JsonNode.Parse(responseBody)
        ?? throw new Exception("Invalid ComfyUI response.");

    var promptId = submission["prompt_id"]?.GetValue<string>()
        ?? throw new Exception("Missing prompt_id.");

    Console.WriteLine($"Prompt ID: {promptId}");
    Console.WriteLine("Waiting for image generation...");

    // 3. Poll history để chờ kết quả.
    JsonNode? completedHistory = null;

    var deadline = DateTime.UtcNow.AddMinutes(20);

    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));

        using var historyResponse = await http.GetAsync(
            $"/history/{promptId}"
        );

        historyResponse.EnsureSuccessStatusCode();

        var historyJson =
            await historyResponse.Content.ReadAsStringAsync();

        var history = JsonNode.Parse(historyJson);

        var entry = history?[promptId];

        if (entry is null)
        {
            Console.WriteLine(
                $"Still processing... {stopwatch.Elapsed:mm\\:ss}"
            );

            continue;
        }

        var status = entry["status"];

        if (status?["status_str"]?.GetValue<string>() == "error" ||
            status?["completed"]?.GetValue<bool>() == false)
        {
            throw new Exception(
                $"ComfyUI execution failed: {status?.ToJsonString()}"
            );
        }

        completedHistory = entry;
        break;
    }

    if (completedHistory is null)
    {
        throw new TimeoutException(
            "ComfyUI did not finish within 20 minutes."
        );
    }

    // 4. Lấy danh sách ảnh từ node Save Image (ID 7).
    var images = completedHistory["outputs"]?["7"]?["images"]
        as JsonArray;

    if (images is null || images.Count == 0)
    {
        throw new Exception(
            "No images found in Save Image node output."
        );
    }

    Directory.CreateDirectory(outputFolder);

    // 5. Tải ảnh qua endpoint /view.
    foreach (var image in images)
    {
        var filename = image?["filename"]?.GetValue<string>()
            ?? throw new Exception("Missing image filename.");

        var subfolder = image?["subfolder"]?.GetValue<string>() ?? "";
        var type = image?["type"]?.GetValue<string>() ?? "output";

        var url =
            "/view?filename=" + Uri.EscapeDataString(filename) +
            "&subfolder=" + Uri.EscapeDataString(subfolder) +
            "&type=" + Uri.EscapeDataString(type);

        using var imageResponse = await http.GetAsync(url);

        imageResponse.EnsureSuccessStatusCode();

        var imageBytes =
            await imageResponse.Content.ReadAsByteArrayAsync();

        var localPath = Path.Combine(
            outputFolder,
            Path.GetFileName(filename)
        );

        await File.WriteAllBytesAsync(localPath, imageBytes);

        Console.WriteLine($"Image saved: {Path.GetFullPath(localPath)}");
    }

    stopwatch.Stop();

    Console.WriteLine(
        $"Completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds."
    );
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Environment.ExitCode = 1;
}

