using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AsciiLandsWindMap;

public sealed class WindDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public bool CreatedNew { get; }

    public WindDatabase(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        CreatedNew = !File.Exists(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Migrate();
    }

    public static string AppFolder() => FolderBesideTheApp();

    public static string DefaultDatabasePath()
    {
        var folder = FolderBesideTheApp();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "wind.sqlite");
        if (!File.Exists(path))
            TryCopyLegacyDatabase(path);
        return path;
    }

    private static string FolderBesideTheApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AsciiLandsWindMap.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryCopyLegacyDatabase(string path)
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsciiLandsWindMap",
            "wind.sqlite");
        if (!File.Exists(legacy) || string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Copy(legacy, path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _connection.Dispose();

    public MapData LoadMap(string realm)
    {
        var regions = new List<RegionRow>();
        var byId = new Dictionary<string, RegionRow>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, q, r, name, biome, note, caption, icon FROM regions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new RegionRow
                {
                    Id = reader.GetString(0),
                    Q = reader.GetInt32(1),
                    R = reader.GetInt32(2),
                    Name = reader.GetString(3),
                    Biome = reader.GetString(4),
                    Note = reader.GetString(5),
                    Caption = reader.GetString(6),
                    Icon = reader.GetString(7),
                };
                regions.Add(row);
                byId[row.Id] = row;
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT region_id, tag FROM region_tags ORDER BY tag";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (byId.TryGetValue(reader.GetString(0), out var row))
                    row.Tags.Add(reader.GetString(1));
            }
        }

        var observations = new Dictionary<string, List<ObservationRow>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, region_id, realm, game_minute, direction_deg, strength, recorded_utc
                FROM observations
                WHERE realm = $realm
                ORDER BY game_minute, id
                """;
            command.Parameters.AddWithValue("$realm", realm);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = ReadObservation(reader);
                if (!observations.TryGetValue(row.RegionId, out var list))
                    observations[row.RegionId] = list = [];
                list.Add(row);
            }
        }

        var syncWind = new Dictionary<string, ObservationRow>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT region_id, realm, game_minute, direction_deg, strength, recorded_utc
                FROM sync_wind
                WHERE realm = $realm
                """;
            command.Parameters.AddWithValue("$realm", realm);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                syncWind[reader.GetString(0)] = new ObservationRow
                {
                    RegionId = reader.GetString(0),
                    Realm = reader.GetString(1),
                    GameMinute = reader.GetInt32(2),
                    DirectionDeg = reader.GetDouble(3),
                    Strength = reader.GetInt32(4),
                    RecordedUtc = reader.GetString(5),
                };
            }
        }

        return new MapData { Regions = regions, Observations = observations, SyncWind = syncWind };
    }

    public List<TileSurvey> LoadSurveys()
    {
        var rows = new List<TileSurvey>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT region_id, edges, png, open_sea FROM tile_survey";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            byte[]? png = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2);
            rows.Add(new TileSurvey
            {
                RegionId = reader.GetString(0),
                Edges = (byte)reader.GetInt32(1),
                Png = png,
                OpenSea = reader.GetInt32(3) != 0,
            });
        }
        return rows;
    }

    public void SaveSurvey(string regionId, byte edges, byte[]? png, bool openSea)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tile_survey (region_id, edges, png, open_sea, updated_utc)
            VALUES ($id, $edges, $png, $sea, $utc)
            ON CONFLICT(region_id) DO UPDATE SET
                edges = excluded.edges,
                png = excluded.png,
                open_sea = excluded.open_sea,
                updated_utc = excluded.updated_utc
            """;
        command.Parameters.AddWithValue("$id", regionId);
        command.Parameters.AddWithValue("$edges", (int)edges);
        var blob = command.Parameters.Add("$png", SqliteType.Blob);
        blob.Value = png == null ? DBNull.Value : png;
        command.Parameters.AddWithValue("$sea", openSea ? 1 : 0);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void ReplaceSyncWind(string realm, int gameMinute, IReadOnlyDictionary<string, SeaWindArrow> arrows)
    {
        gameMinute = WindRules.NormalizeMinute(gameMinute);
        var utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        using var transaction = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sync_wind WHERE realm = $realm";
            delete.Parameters.AddWithValue("$realm", realm);
            delete.ExecuteNonQuery();
        }
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sync_wind (region_id, realm, game_minute, direction_deg, strength, recorded_utc)
                VALUES ($id, $realm, $minute, $dir, $strength, $utc)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var realmParam = insert.Parameters.Add("$realm", SqliteType.Text);
            var minute = insert.Parameters.Add("$minute", SqliteType.Integer);
            var dir = insert.Parameters.Add("$dir", SqliteType.Real);
            var strength = insert.Parameters.Add("$strength", SqliteType.Integer);
            var recorded = insert.Parameters.Add("$utc", SqliteType.Text);
            foreach (var (regionId, arrow) in arrows)
            {
                id.Value = regionId;
                realmParam.Value = realm;
                minute.Value = gameMinute;
                dir.Value = Math.Round(CompassText.Normalize(arrow.DirectionDeg), 1);
                strength.Value = Math.Clamp(arrow.Strength, 1, 3);
                recorded.Value = utc;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public void SaveSyncTile(string realm, string regionId, int gameMinute, double directionDeg, int strength)
    {
        gameMinute = WindRules.NormalizeMinute(gameMinute);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_wind (region_id, realm, game_minute, direction_deg, strength, recorded_utc)
            VALUES ($id, $realm, $minute, $dir, $strength, $utc)
            ON CONFLICT(region_id, realm) DO UPDATE SET
                game_minute = excluded.game_minute,
                direction_deg = excluded.direction_deg,
                strength = excluded.strength,
                recorded_utc = excluded.recorded_utc
            """;
        command.Parameters.AddWithValue("$id", regionId);
        command.Parameters.AddWithValue("$realm", realm);
        command.Parameters.AddWithValue("$minute", gameMinute);
        command.Parameters.AddWithValue("$dir", Math.Round(CompassText.Normalize(directionDeg), 1));
        command.Parameters.AddWithValue("$strength", Math.Clamp(strength, 1, 3));
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public SyncResult ApplyWorld(IReadOnlyList<WorldNode> nodes, string? worldId)
    {
        var valid = new List<WorldNode>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Coord == null || node.Coord.Length < 2)
                continue;
            valid.Add(node);
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in valid)
        {
            var raw = node.Name ?? "";
            nameCounts[raw] = nameCounts.GetValueOrDefault(raw) + 1;
        }

        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name FROM regions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                existing[reader.GetString(0)] = reader.GetString(1);
        }

        var added = 0;
        var renamed = 0;
        var utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        using var transaction = _connection.BeginTransaction();
        using var upsert = _connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO regions (id, q, r, name, biome, note, updated_utc)
            VALUES ($id, $q, $r, $name, $biome, '', $utc)
            ON CONFLICT(id) DO UPDATE SET
                q = excluded.q,
                r = excluded.r,
                name = excluded.name,
                biome = excluded.biome,
                updated_utc = excluded.updated_utc
            """;
        var idParam = upsert.Parameters.Add("$id", SqliteType.Text);
        var qParam = upsert.Parameters.Add("$q", SqliteType.Integer);
        var rParam = upsert.Parameters.Add("$r", SqliteType.Integer);
        var nameParam = upsert.Parameters.Add("$name", SqliteType.Text);
        var biomeParam = upsert.Parameters.Add("$biome", SqliteType.Text);
        var utcParam = upsert.Parameters.Add("$utc", SqliteType.Text);

        foreach (var node in valid)
        {
            var raw = node.Name ?? "";
            var display = WorldNames.Display(raw, node.Id, nameCounts[raw] > 1);
            var biome = string.IsNullOrWhiteSpace(node.Biome) ? "unknown" : node.Biome.Trim();
            if (existing.TryGetValue(node.Id, out var oldName))
            {
                if (WorldNames.IsPlaceholder(raw) && !WorldNames.IsPlaceholder(oldName))
                    display = oldName;
                if (!string.Equals(oldName, display, StringComparison.Ordinal))
                    renamed++;
            }
            else
            {
                added++;
            }

            idParam.Value = node.Id;
            qParam.Value = node.Coord![0];
            rParam.Value = node.Coord[1];
            nameParam.Value = display;
            biomeParam.Value = biome;
            utcParam.Value = utc;
            upsert.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(worldId))
            SetSetting("world_id", worldId, transaction);
        transaction.Commit();
        return new SyncResult(added, renamed, valid.Count, worldId);
    }

    public void SaveTileMeta(string regionId, string note, IEnumerable<string> tags, string caption = "", string icon = "")
    {
        note = note.Trim();
        if (note.Length > 2000)
            note = note[..2000];
        caption = TileIcons.CleanCaption(caption);
        icon = TileIcons.Canonical(icon) ?? "";
        var canonical = TileTags.CanonicalSet(tags);
        using var transaction = _connection.BeginTransaction();
        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE regions SET note = $note, caption = $caption, icon = $icon WHERE id = $id";
            update.Parameters.AddWithValue("$note", note);
            update.Parameters.AddWithValue("$caption", caption);
            update.Parameters.AddWithValue("$icon", icon);
            update.Parameters.AddWithValue("$id", regionId);
            update.ExecuteNonQuery();
        }
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM region_tags WHERE region_id = $id";
            delete.Parameters.AddWithValue("$id", regionId);
            delete.ExecuteNonQuery();
        }
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO region_tags (region_id, tag) VALUES ($id, $tag)";
            var idParam = insert.Parameters.Add("$id", SqliteType.Text);
            var tagParam = insert.Parameters.Add("$tag", SqliteType.Text);
            foreach (var tag in canonical)
            {
                idParam.Value = regionId;
                tagParam.Value = tag;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public ObservationRow SaveObservation(string regionId, string realm, int gameMinute, double directionDeg, int strength)
    {
        gameMinute = WindRules.NormalizeMinute(gameMinute);
        directionDeg = CompassText.Normalize(directionDeg);
        strength = Math.Clamp(strength, 0, 3);
        var utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        using var find = _connection.CreateCommand();
        find.CommandText = """
            SELECT id FROM observations
            WHERE region_id = $id AND realm = $realm AND game_minute = $minute
            """;
        find.Parameters.AddWithValue("$id", regionId);
        find.Parameters.AddWithValue("$realm", realm);
        find.Parameters.AddWithValue("$minute", gameMinute);
        var existing = find.ExecuteScalar();
        if (existing != null && existing != DBNull.Value)
        {
            var id = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            using var update = _connection.CreateCommand();
            update.CommandText = """
                UPDATE observations
                SET direction_deg = $dir, strength = $strength, recorded_utc = $utc
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$dir", directionDeg);
            update.Parameters.AddWithValue("$strength", strength);
            update.Parameters.AddWithValue("$utc", utc);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            return new ObservationRow
            {
                Id = id,
                RegionId = regionId,
                Realm = realm,
                GameMinute = gameMinute,
                DirectionDeg = directionDeg,
                Strength = strength,
                RecordedUtc = utc,
            };
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO observations (region_id, realm, game_minute, direction_deg, strength, recorded_utc)
                VALUES ($id, $realm, $minute, $dir, $strength, $utc)
                """;
            insert.Parameters.AddWithValue("$id", regionId);
            insert.Parameters.AddWithValue("$realm", realm);
            insert.Parameters.AddWithValue("$minute", gameMinute);
            insert.Parameters.AddWithValue("$dir", directionDeg);
            insert.Parameters.AddWithValue("$strength", strength);
            insert.Parameters.AddWithValue("$utc", utc);
            insert.ExecuteNonQuery();
        }
        using var idCommand = _connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid()";
        var newId = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new ObservationRow
        {
            Id = newId,
            RegionId = regionId,
            Realm = realm,
            GameMinute = gameMinute,
            DirectionDeg = directionDeg,
            Strength = strength,
            RecordedUtc = utc,
        };
    }

    public void DeleteObservation(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM observations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public MapSnapshot Export(string realm, string worldId)
    {
        var map = LoadMap(realm);
        var tiles = new List<TileSnapshot>();
        foreach (var region in map.Regions)
        {
            if (!HasContent(region.Note, region.Caption, region.Icon, region.Tags))
                continue;
            tiles.Add(new TileSnapshot
            {
                Id = region.Id,
                Note = region.Note.Trim(),
                Caption = TileIcons.CleanCaption(region.Caption),
                Icon = TileIcons.Canonical(region.Icon) ?? "",
                Tags = region.Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
                Observations = [],
            });
        }
        tiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return new MapSnapshot
        {
            Schema = 1,
            App = "AsciiLandsWindMap",
            Realm = realm,
            WorldId = worldId,
            ExportedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Tiles = tiles,
        };
    }

    public ImportAnalysis Analyze(MapSnapshot snapshot)
    {
        var regions = new Dictionary<string, (string Name, string Note, string Caption, string Icon)>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, note, caption, icon FROM regions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                regions[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }

        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT region_id, tag FROM region_tags";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!tags.TryGetValue(id, out var list))
                    tags[id] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        var adds = new List<TileSnapshot>();
        var conflicts = new List<ImportConflict>();
        var unknown = new List<string>();
        var unchanged = 0;
        foreach (var tile in snapshot.Tiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(tile.Id))
                continue;
            var theirs = NormalizeTile(tile);
            if (!HasContent(theirs))
                continue;
            if (!regions.TryGetValue(tile.Id, out var region))
            {
                unknown.Add(tile.Id);
                continue;
            }
            tags.TryGetValue(tile.Id, out var localTags);
            localTags ??= [];
            var localNote = region.Note ?? "";
            var localCaption = region.Caption ?? "";
            var localIcon = region.Icon ?? "";
            if (!HasContent(localNote, localCaption, localIcon, localTags))
            {
                adds.Add(theirs);
                continue;
            }
            if (SameTile(localNote, localCaption, localIcon, localTags, theirs))
            {
                unchanged++;
                continue;
            }
            conflicts.Add(new ImportConflict
            {
                RegionId = tile.Id,
                DisplayName = region.Name,
                LocalSummary = TileText.Summary(localNote, localCaption, localIcon, TileTags.CanonicalSet(localTags)),
                TheirSummary = TileText.Summary(theirs.Note, theirs.Caption, theirs.Icon, theirs.Tags),
                Theirs = theirs,
            });
        }

        conflicts.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new ImportAnalysis
        {
            Adds = adds,
            Conflicts = conflicts,
            Unchanged = unchanged,
            UnknownIds = unknown,
        };
    }

    public ImportApplyResult ApplyImport(MapSnapshot snapshot, IReadOnlySet<string> replaceIds)
    {
        var analysis = Analyze(snapshot);
        var added = 0;
        var replaced = 0;
        var kept = 0;
        using var transaction = _connection.BeginTransaction();
        foreach (var tile in analysis.Adds)
        {
            WriteTile(transaction, tile);
            added++;
        }
        foreach (var conflict in analysis.Conflicts)
        {
            if (replaceIds.Contains(conflict.RegionId))
            {
                WriteTile(transaction, conflict.Theirs);
                replaced++;
            }
            else
            {
                kept++;
            }
        }
        transaction.Commit();
        return new ImportApplyResult(added, replaced, kept, analysis.Unchanged, analysis.UnknownIds.Count);
    }

    public string? GetSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value) => SetSetting(key, value, null);

    public ClockSettings LoadClock()
    {
        DateTime? anchor = null;
        if (DateTime.TryParse(GetSetting("clock_anchor_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            anchor = parsed.ToUniversalTime();
        int? minute = int.TryParse(GetSetting("clock_anchor_minute"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMinute)
            ? parsedMinute
            : null;
        double? length = null;
        if (double.TryParse(GetSetting("day_length_minutes"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLength) && parsedLength > 0)
            length = parsedLength;
        return new ClockSettings(anchor, minute, length);
    }

    public void SaveClock(ClockSettings clock)
    {
        SetSetting("clock_anchor_utc", clock.AnchorUtc?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        SetSetting("clock_anchor_minute", clock.AnchorMinute?.ToString(CultureInfo.InvariantCulture));
        SetSetting("day_length_minutes", clock.DayLengthMinutes?.ToString(CultureInfo.InvariantCulture));
    }

    private void SetSetting(string key, string? value, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (value is null)
        {
            command.CommandText = "DELETE FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
        }
        else
        {
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
        }
        command.ExecuteNonQuery();
    }

    private void WriteTile(SqliteTransaction transaction, TileSnapshot tile)
    {
        var note = tile.Note?.Trim() ?? "";
        if (note.Length > 2000)
            note = note[..2000];
        var caption = TileIcons.CleanCaption(tile.Caption);
        var icon = TileIcons.Canonical(tile.Icon) ?? "";
        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE regions SET note = $note, caption = $caption, icon = $icon WHERE id = $id";
            update.Parameters.AddWithValue("$note", note);
            update.Parameters.AddWithValue("$caption", caption);
            update.Parameters.AddWithValue("$icon", icon);
            update.Parameters.AddWithValue("$id", tile.Id);
            update.ExecuteNonQuery();
        }
        using (var deleteTags = _connection.CreateCommand())
        {
            deleteTags.Transaction = transaction;
            deleteTags.CommandText = "DELETE FROM region_tags WHERE region_id = $id";
            deleteTags.Parameters.AddWithValue("$id", tile.Id);
            deleteTags.ExecuteNonQuery();
        }
        using (var insertTag = _connection.CreateCommand())
        {
            insertTag.Transaction = transaction;
            insertTag.CommandText = "INSERT INTO region_tags (region_id, tag) VALUES ($id, $tag)";
            var idParam = insertTag.Parameters.Add("$id", SqliteType.Text);
            var tagParam = insertTag.Parameters.Add("$tag", SqliteType.Text);
            foreach (var tag in TileTags.CanonicalSet(tile.Tags ?? []))
            {
                idParam.Value = tile.Id;
                tagParam.Value = tag;
                insertTag.ExecuteNonQuery();
            }
        }
    }

    private static TileSnapshot NormalizeTile(TileSnapshot tile)
    {
        return new TileSnapshot
        {
            Id = tile.Id,
            Note = (tile.Note ?? "").Trim(),
            Caption = TileIcons.CleanCaption(tile.Caption),
            Icon = TileIcons.Canonical(tile.Icon) ?? "",
            Tags = TileTags.CanonicalSet(tile.Tags ?? []),
            Observations = [],
        };
    }

    private static bool HasContent(TileSnapshot tile) =>
        HasContent(tile.Note, tile.Caption, tile.Icon, tile.Tags);

    private static bool HasContent(string note, string caption, string icon, IReadOnlyCollection<string> tags) =>
        note.Trim().Length > 0
        || TileIcons.CleanCaption(caption).Length > 0
        || TileIcons.Canonical(icon) != null
        || tags.Count > 0;

    private static bool SameTile(string note, string caption, string icon, IReadOnlyList<string> tags, TileSnapshot theirs)
    {
        if (!string.Equals(note.Trim(), theirs.Note.Trim(), StringComparison.Ordinal))
            return false;
        if (!string.Equals(TileIcons.CleanCaption(caption), TileIcons.CleanCaption(theirs.Caption), StringComparison.Ordinal))
            return false;
        if (!string.Equals(TileIcons.Canonical(icon) ?? "", TileIcons.Canonical(theirs.Icon) ?? "", StringComparison.Ordinal))
            return false;
        var localTags = TileTags.CanonicalSet(tags);
        if (localTags.Count != theirs.Tags.Count)
            return false;
        var theirTags = new HashSet<string>(theirs.Tags, StringComparer.OrdinalIgnoreCase);
        return localTags.All(tag => theirTags.Contains(tag));
    }

    private static ObservationRow ReadObservation(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        RegionId = reader.GetString(1),
        Realm = reader.GetString(2),
        GameMinute = reader.GetInt32(3),
        DirectionDeg = reader.GetDouble(4),
        Strength = reader.GetInt32(5),
        RecordedUtc = reader.GetString(6),
    };

    private void Migrate()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS regions (
                id TEXT PRIMARY KEY,
                q INTEGER NOT NULL,
                r INTEGER NOT NULL,
                name TEXT NOT NULL,
                biome TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                caption TEXT NOT NULL DEFAULT '',
                icon TEXT NOT NULL DEFAULT '',
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS region_tags (
                region_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY (region_id, tag)
            );
            CREATE TABLE IF NOT EXISTS observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                region_id TEXT NOT NULL,
                realm TEXT NOT NULL,
                game_minute INTEGER NOT NULL,
                direction_deg REAL NOT NULL,
                strength INTEGER NOT NULL,
                recorded_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_obs_region ON observations(region_id, realm);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_wind (
                region_id TEXT NOT NULL,
                realm TEXT NOT NULL,
                game_minute INTEGER NOT NULL,
                direction_deg REAL NOT NULL,
                strength INTEGER NOT NULL,
                recorded_utc TEXT NOT NULL,
                PRIMARY KEY (region_id, realm)
            );
            CREATE TABLE IF NOT EXISTS tile_survey (
                region_id TEXT PRIMARY KEY,
                edges INTEGER NOT NULL,
                png BLOB,
                open_sea INTEGER NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn("caption", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("icon", "TEXT NOT NULL DEFAULT ''");
    }

    private void EnsureColumn(string column, string definition)
    {
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(regions)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE regions ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
