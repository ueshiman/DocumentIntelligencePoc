using Azure;
using Azure.AI.DocumentIntelligence;
using System.Text;
using System.Text.Json;

const string endpoint = "https://pocintelligence.cognitiveservices.azure.com/";

const string inputFolder = "input";
const string outputFolder = "output";

string key = Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_KEY")
    ?? throw new InvalidOperationException("環境変数 'AZURE_DOCUMENT_INTELLIGENCE_KEY' が設定されていません。");

var client = new DocumentIntelligenceClient(
    new Uri(endpoint),
    new AzureKeyCredential(key));

if (!Directory.Exists(inputFolder))
{
    Console.WriteLine($"input フォルダーが見つかりません: {Path.GetFullPath(inputFolder)}");
    return;
}

Directory.CreateDirectory(outputFolder);

string[] targetExtensions =
[
    ".pdf",
    //".png",
    //".jpg",
    //".jpeg",
    //".tif",
    //".tiff",
    //".bmp",
    ".docx",
    ".docm",
    ".dotx",
    ".dotm",
    ".doc",
    ".dot",
    ".xlsx",
    ".xls",
    ".xlsm",
    ".xlsb",
    ".xltx",        
    ".xltm",
    ".csv",
    ".xlt",
    ".pptx",
    ".ppt",
    ".pptm",
    ".ppsx",
    ".ppsm",
    ".potx",
    ".potm",
    ".html"
];

var files = Directory
    .EnumerateFiles(inputFolder, "*.*", SearchOption.AllDirectories)
    .Where(file => targetExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
    .ToList();

if (files.Count == 0)
{
    Console.WriteLine("処理対象ファイルが見つかりません。");
    Console.WriteLine("input フォルダーに PDF / Office / 画像ファイルを配置してください。");
    return;
}

Console.WriteLine($"処理対象ファイル数: {files.Count}");
Console.WriteLine();

int successCount = 0;
int failureCount = 0;

foreach (string filePath in files)
{
    string fileName = Path.GetFileName(filePath);
    //string baseName = Path.GetFileNameWithoutExtension(filePath);
    string baseName = Path.GetFileName(filePath);

    string safeBaseName = MakeSafeFileName(baseName);
    string fileOutputFolder = Path.Combine(outputFolder, safeBaseName);

    Directory.CreateDirectory(fileOutputFolder);

    string textPath = Path.Combine(fileOutputFolder, "result.txt");
    string markdownPath = Path.Combine(fileOutputFolder, "result.md");
    string summaryPath = Path.Combine(fileOutputFolder, "summary.txt");
    string jsonPath = Path.Combine(fileOutputFolder, "result.json");
    string errorPath = Path.Combine(fileOutputFolder, "error.txt");

    Console.WriteLine($"処理開始: {fileName}");

    try
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        BinaryData bytesSource = BinaryData.FromBytes(fileBytes);

        var options = new AnalyzeDocumentOptions("prebuilt-layout", bytesSource)
        {
            // 構造付きTEXTとして Markdown 形式でも取得する
            OutputContentFormat = DocumentContentFormat.Markdown

            // 無料枠や動作確認でページを絞りたい場合は有効化
            // Pages = "1-2"
        };

        Operation<AnalyzeResult> operation =
            await client.AnalyzeDocumentAsync(WaitUntil.Completed, options);

        AnalyzeResult result = operation.Value;

        // 主出力: TEXT
        // Markdown指定時の Content は Markdown風の構造付きテキストになる。
        // まずはこれを result.txt に保存して、検索/RAG前処理の元データとして評価する。
        await File.WriteAllTextAsync(
            textPath,
            result.Content,
            Encoding.UTF8);

        // 補助出力: Markdown
        await File.WriteAllTextAsync(
            markdownPath,
            result.Content,
            Encoding.UTF8);

        // 解析概要
        var summary = new StringBuilder();

        summary.AppendLine($"FileName: {fileName}");
        summary.AppendLine($"ModelId: {result.ModelId}");
        summary.AppendLine($"Pages: {result.Pages.Count}");
        summary.AppendLine($"Paragraphs: {result.Paragraphs.Count}");
        summary.AppendLine($"Tables: {result.Tables.Count}");
        summary.AppendLine();

        for (int i = 0; i < result.Tables.Count; i++)
        {
            var table = result.Tables[i];
            summary.AppendLine($"Table {i + 1}: {table.RowCount} rows x {table.ColumnCount} columns");
        }

        await File.WriteAllTextAsync(
            summaryPath,
            summary.ToString(),
            Encoding.UTF8);

        // 必要に応じて解析結果全体を JSON 保存
        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        await File.WriteAllTextAsync(
            jsonPath,
            json,
            Encoding.UTF8);

        if (File.Exists(errorPath))
        {
            File.Delete(errorPath);
        }

        successCount++;

        Console.WriteLine($"成功: {fileName}");
        Console.WriteLine($"  TEXT: {textPath}");
        Console.WriteLine();
    }
    catch (RequestFailedException ex)
    {
        failureCount++;

        string errorText =
$"""
FileName: {fileName}
Status: {ex.Status}
ErrorCode: {ex.ErrorCode}

Message:
{ex.Message}
""";

        await File.WriteAllTextAsync(errorPath, errorText, Encoding.UTF8);

        Console.WriteLine($"失敗: {fileName}");
        Console.WriteLine($"  Status: {ex.Status}");
        Console.WriteLine($"  Error: {ex.Message}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        failureCount++;

        string errorText =
$"""
FileName: {fileName}

Exception:
{ex}
""";

        await File.WriteAllTextAsync(errorPath, errorText, Encoding.UTF8);

        Console.WriteLine($"失敗: {fileName}");
        Console.WriteLine($"  Error: {ex.Message}");
        Console.WriteLine();
    }
}

Console.WriteLine("一括処理が完了しました。");
Console.WriteLine($"成功: {successCount}");
Console.WriteLine($"失敗: {failureCount}");
Console.WriteLine($"出力先: {Path.GetFullPath(outputFolder)}");

static string MakeSafeFileName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars())
    {
        name = name.Replace(c, '_');
    }

    return name;
}