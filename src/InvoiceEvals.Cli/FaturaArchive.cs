using System.Security.Cryptography;

namespace InvoiceEvals.Cli;

/// <summary>Downloads and verifies the FATURA archive (Zenodo record 10371464, CC BY 4.0).</summary>
internal sealed class FaturaArchive(HttpClient http, TextWriter log)
{
    private const string Url = "https://zenodo.org/records/10371464/files/FATURA2.zip?download=1";

    // Pinned from a download whose MD5 matched the one Zenodo publishes (4c9404462f22c5241eb1a290a02eb2a2).
    private const string Sha256 = "984e3812b8b7c632e668a2dd92afab5ae44631face24efe53e543910e5fca474";

    public async Task<string> EnsureAsync(string cacheDir, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "FATURA2.zip");
        if (!File.Exists(path))
        {
            log.WriteLine($"Downloading {Url} (~690 MB) to {path}");
            var part = path + ".part";
            using (var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(part);
                await source.CopyToAsync(target, ct);
            }
            File.Move(part, path, overwrite: true);
        }

        log.WriteLine("Verifying SHA-256");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (actual != Sha256)
            throw new InvalidDataException($"Checksum mismatch for {path}: expected {Sha256}, got {actual}. Delete the file and retry.");
        return path;
    }
}
