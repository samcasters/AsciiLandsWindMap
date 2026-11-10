using System.Text.Json;

namespace AsciiLandsWindMap;

public sealed class MapFileException : Exception
{
    public MapFileException(string message) : base(message)
    {
    }
}

public static class MapExchange
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write(string path, MapSnapshot snapshot)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public static MapSnapshot Read(string path)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<MapSnapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot == null)
                throw new MapFileException("That file is empty.");
            if (snapshot.Schema != 1)
                throw new MapFileException("This file is from a different version of the tile notes.");
            if (!Realms.IsKnown(snapshot.Realm))
                throw new MapFileException("This file does not say which realm it is for.");
            snapshot.Tiles ??= [];
            return snapshot;
        }
        catch (MapFileException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new MapFileException("That file is not a tile notes JSON file.");
        }
        catch (IOException ex)
        {
            throw new MapFileException(ex.Message);
        }
    }
}
