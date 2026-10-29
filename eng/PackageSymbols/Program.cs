using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("Usage: PackageSymbols <package-directory>");
var packages = Directory.GetFiles(args[0], "*.nupkg");
if (packages.Length != 4) throw new InvalidDataException("Expected four release packages.");
var embeddedSourceKind = new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
var sourceLinkKind = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
foreach (var package in packages)
{
    using var zip = ZipFile.OpenRead(package);
    var library = zip.Entries.Single(entry => entry.FullName.StartsWith("lib/", StringComparison.Ordinal) && entry.FullName.EndsWith(".dll", StringComparison.Ordinal));
    using var image = new MemoryStream();
    using (var stream = library.Open()) stream.CopyTo(image);
    image.Position = 0;
    using var pe = new PEReader(image);
    var entry = pe.ReadDebugDirectory().Single(item => item.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    using var symbols = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
    var metadata = symbols.GetMetadataReader();
    if (metadata.Documents.Count == 0) throw new InvalidDataException("No source documents in embedded PDB.");
    foreach (var document in metadata.Documents)
    {
        if (!metadata.GetCustomDebugInformation(document).Any(handle =>
            metadata.GetGuid(metadata.GetCustomDebugInformation(handle).Kind) == embeddedSourceKind))
            throw new InvalidDataException("A source document is not embedded.");
    }
    var link = metadata.CustomDebugInformation.Select(metadata.GetCustomDebugInformation)
        .Single(item => metadata.GetGuid(item.Kind) == sourceLinkKind);
    using var sourceLink = JsonDocument.Parse(metadata.GetBlobBytes(link.Value));
    if (!sourceLink.RootElement.GetProperty("documents").EnumerateObject().Any())
        throw new InvalidDataException("SourceLink document mappings are empty.");
    Console.WriteLine($"{Path.GetFileName(package)}: embedded PDB, {metadata.Documents.Count} embedded documents, SourceLink");
}
