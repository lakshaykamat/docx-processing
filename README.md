# DOCX Processing

A read-only DOCX viewer focused on the document hierarchy and the styles used by its content. The interface places the content tree beside a readable preview of the document's main body.

## Run

Install the **.NET 10 SDK**, then from the repository directory:

```powershell
dotnet restore DocxProcessing.slnx
dotnet run --project src/DocxProcessing --urls http://localhost:5080
```

Open **http://localhost:5080**. Choose a `.docx` file up to 20 MB. The file is inspected in memory and is not saved or modified.

For AI style evaluation, put your API key in the repository-root `.env` file:

```text
OPENAI_API_KEY=your-key-here
```

Restart the app after changing `.env`. `OPENAI_MODEL` can optionally select another model available to your API account; the default is `gpt-5-nano`. The key stays on the server and is never sent to the browser. When you click **Evaluate styles with OpenAI**, the app sends sampled main-body text and applied style names to OpenAI for review; this may use API quota or incur charges. The document is not modified.

## Use

- Browse the document's content hierarchy with each paragraph's style shown separately from character styles on text runs. Expand a paragraph's character-style list to see the text-to-style mapping.
- Use the **Heading order** tab to see only heading text, listed in document order, indented by level, and numbered hierarchically (for example, `2.1`).
- Only styles used in the document appear in the tree. Expand or collapse content branches, or search content and style names.
- Select a tree item to locate and highlight it in the preview.
- Read the main document body next to the tree, including headings, paragraphs, basic text formatting, and tables.
- Run an optional OpenAI style review that checks whether each main-body text block fits its applied paragraph or table style. Large reviews are split into bounded requests. Findings show the text, applied style, a brief reason, and a link back to the document item.

The preview is a readable reconstruction, not Microsoft Word's page layout. It does not currently render headers, footers, images, page breaks, or all Word-specific layout and table styling.

## Implementation

- `Program.cs` serves the static UI and a multipart `POST /api/inspect` endpoint.
- `POST /api/evaluate-style` reanalyzes the uploaded DOCX and sends all main-body review items in bounded batches to the OpenAI Responses API. Each item includes its location and nearby structural context. It uses structured JSON output for the findings and does not send the API key to the client.
- `Prompts/style-evaluation.txt` contains the evaluator's complete system prompt and is copied into build and publish output.
- `DocumentAnalyzer.cs` reads the DOCX package with the Open XML SDK and produces used-style and main-body hierarchy data for the UI. It recognizes standard heading names and common `Head1`-style custom names when explicit outline levels are absent.
- `StyleEvaluator.cs` sends all defined style IDs, names, and types plus main-body items in batches of up to 40. Items include their section or table-cell context; formatting and style-inheritance details stay out of the model input.
- `wwwroot` contains the dependency-free HTML, CSS, and JavaScript interface. Uploaded strings are inserted as text, not interpreted as HTML.
- `tests/DocxProcessing.Tests` contains the analyzer's xUnit tests.

Uploads are held in memory for the request. The inspector opens the package read-only and does not change the input bytes. Limits are 20 MB compressed upload, 100 MB expanded package, 5,000 ZIP entries, and 30 million XML characters per part.
