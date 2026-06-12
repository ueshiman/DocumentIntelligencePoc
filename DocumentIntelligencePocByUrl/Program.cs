using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using System.ClientModel.Primitives;

static string GetRequiredEnv(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"環境変数 '{name}' が設定されていません。");
    }

    return value;
}

string documentIntelligenceEndpoint = GetRequiredEnv("DOCINTEL_ENDPOINT");
string documentIntelligenceKey = GetRequiredEnv("DOCINTEL_KEY");

string storageConnectionString = GetRequiredEnv("BLOB_CONNECTION_STRING");
string containerName = GetRequiredEnv("BLOB_CONTAINER");

// 空ならコンテナー全体。
// 例: "input/" を指定すると input/ 配下だけ処理。
string? blobPrefix = Environment.GetEnvironmentVariable("BLOB_PREFIX");

string outputDirectory = $"{Environment.GetEnvironmentVariable("OUTPUT_DIR") ?? Path.Combine(Environment.CurrentDirectory, "docintel-output")}{DateTimeOffset.Now:yyyyMMddHHmmss}";

// 並列数。まずは 1 または 2 が無難。
int maxDegreeOfParallelism =
    int.TryParse(Environment.GetEnvironmentVariable("MAX_PARALLEL"), out int parsedParallel)
        ? parsedParallel
        : 2;

Directory.CreateDirectory(outputDirectory);
Console.WriteLine($"Output directory: {outputDirectory}");

DocumentIntelligenceClient documentClient = new DocumentIntelligenceClient(
    new Uri(documentIntelligenceEndpoint),
    new AzureKeyCredential(documentIntelligenceKey));

BlobContainerClient containerClient = new BlobContainerClient(
    storageConnectionString,
    containerName);

if (!await containerClient.ExistsAsync())
{
    throw new InvalidOperationException(
        $"コンテナー '{containerName}' が存在しません。BLOB_CONNECTION_STRING の Storage Account と BLOB_CONTAINER を確認してください。");
}

ConcurrentBag<string> logLines = new();

await Parallel.ForEachAsync(
    EnumerateTargetBlobsAsync(containerClient, blobPrefix),
    new ParallelOptions
    {
        MaxDegreeOfParallelism = maxDegreeOfParallelism
    },
    async (blobItem, cancellationToken) =>
    {
        try
        {
            await AnalyzeBlobAndSaveAsync(
                containerClient,
                documentClient,
                blobItem,
                outputDirectory,
                cancellationToken);

            logLines.Add($"OK\t{blobItem.Name}");
        }
        catch (Exception ex)
        {
            logLines.Add($"ERROR\t{blobItem.Name}\t{ex.GetType().Name}\t{ex.Message}");
            Console.WriteLine($"ERROR: {blobItem.Name}");
            Console.WriteLine(ex.Message);
        }
    });

string logPath = Path.Combine(outputDirectory, $"_batch-log{DateTimeOffset.Now:yyyyMMddHHmmss}.tsv");
await File.WriteAllLinesAsync(
    logPath,
    logLines.OrderBy(x => x),
    Encoding.UTF8);

Console.WriteLine("Batch completed.");
Console.WriteLine($"Output: {outputDirectory}");
Console.WriteLine($"Log   : {logPath}");

static async IAsyncEnumerable<BlobItem> EnumerateTargetBlobsAsync(
    BlobContainerClient containerClient,
    string? prefix,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await foreach (BlobItem blobItem in containerClient.GetBlobsAsync(
                       BlobTraits.None,
                       BlobStates.None,
                       prefix,
                       cancellationToken))
    {
        if (blobItem.Name.EndsWith("/", StringComparison.Ordinal))
        {
            continue;
        }

        if (!IsSupportedFile(blobItem.Name))
        {
            Console.WriteLine($"SKIP unsupported: {blobItem.Name}");
            continue;
        }

        yield return blobItem;
    }
}

static bool IsSupportedFile(string blobName)
{
    string extension = Path.GetExtension(blobName).ToLowerInvariant();

    return extension switch
    {
        ".pdf" => true,
        ".jpg" => true,
        ".jpeg" => true,
        ".png" => true,
        ".bmp" => true,
        ".tif" => true,
        ".tiff" => true,
        ".heif" => true,
        ".docx" => true,
        ".xlsx" => true,
        ".pptx" => true,
        ".html" => true,
        ".htm" => true,
        ".doc" => true,
        ".ppt" => true,
        ".xls" => true,

        // .xls は古い形式で失敗しやすいため、まずは対象外にする
        // ".xls" => true,

        _ => false
    };
}

static async Task AnalyzeBlobAndSaveAsync(
    BlobContainerClient containerClient,
    DocumentIntelligenceClient documentClient,
    BlobItem blobItem,
    string outputDirectory,
    CancellationToken cancellationToken)
{
    BlobClient blobClient = containerClient.GetBlobClient(blobItem.Name);

    Uri sourceUri = CreateReadOnlySasUri(blobClient);

    AnalyzeDocumentOptions options = new AnalyzeDocumentOptions(
        "prebuilt-layout",
        sourceUri)
    {
        OutputContentFormat = DocumentContentFormat.Markdown

        // 無料枠で確認する場合などはページを絞れる
        // Pages = "1-2"
    };

    Operation<AnalyzeResult> operation =
        await documentClient.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            options,
            cancellationToken);

    AnalyzeResult result = operation.Value;

    string safeRelativePath = ToSafeRelativePath(blobItem.Name);

    // 例:
    // input/sample.pdf
    // → C:\temp\out\input\sample.pdf.md
    // → C:\temp\out\input\sample.pdf.json
    string outputBasePath = Path.Combine(outputDirectory, safeRelativePath);

    string markdownPath = outputBasePath + ".md";
    string jsonPath = outputBasePath + ".json";

    Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);

    await File.WriteAllTextAsync(
        markdownPath,
        result.Content ?? string.Empty,
        Encoding.UTF8,
        cancellationToken);

    JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };
    jsonOptions.Converters.Add(new JsonModelConverter());

    string json = JsonSerializer.Serialize(result, jsonOptions);

    await File.WriteAllTextAsync(
        jsonPath,
        json,
        Encoding.UTF8,
        cancellationToken);

    Console.WriteLine($"OK: {blobItem.Name}");
}

static Uri CreateReadOnlySasUri(BlobClient blobClient)
{
    if (!blobClient.CanGenerateSasUri)
    {
        throw new InvalidOperationException(
            "SAS URI を生成できません。BLOB_CONNECTION_STRING にストレージアカウントキー付きの接続文字列を指定してください。");
    }

    BlobSasBuilder sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = blobClient.BlobContainerName,
        BlobName = blobClient.Name,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
    };

    sasBuilder.SetPermissions(BlobSasPermissions.Read);

    return blobClient.GenerateSasUri(sasBuilder);
}

static string ToSafeRelativePath(string blobName)
{
    string[] parts = blobName
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(SanitizePathPart)
        .ToArray();

    return Path.Combine(parts);
}

//static string SanitizedFallbackName = "_";

static string SanitizePathPart(string value)
{
    char[] invalidChars = Path.GetInvalidFileNameChars();

    string sanitized = new string(
        value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());

    return string.IsNullOrWhiteSpace(sanitized)
        ? "_"
        : sanitized;
}