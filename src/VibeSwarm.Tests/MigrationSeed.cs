namespace VibeSwarm.Tests;

/// <summary>
/// Seeds rows into a database stopped part-way through its migrations. Such a test can't insert
/// through EF: the current model names columns that later migrations add.
/// </summary>
internal static class MigrationSeed
{
	public static string SqliteGuid(Guid id) => id.ToString().ToUpperInvariant();

	/// <summary>
	/// Inserts a row naming only the given columns; every other required column without a
	/// default gets an empty value of its type, so the insert fits the table whatever its age.
	/// </summary>
	public static void InsertRow(System.Data.Common.DbConnection connection, string table, Dictionary<string, object> values)
	{
		var columns = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
		using (var info = connection.CreateCommand())
		{
			info.CommandText = $"PRAGMA table_info(\"{table}\")";
			using var reader = info.ExecuteReader();
			while (reader.Read())
			{
				var name = reader.GetString(1);
				var type = reader.GetString(2).ToUpperInvariant();
				var notNull = reader.GetInt64(3) == 1;
				var hasDefault = !reader.IsDBNull(4);
				var isKey = reader.GetInt64(5) > 0;
				if (columns.ContainsKey(name) || hasDefault || (isKey && type == "INTEGER") || (!notNull && !isKey))
				{
					continue;
				}

				columns[name] = type switch
				{
					"INTEGER" => 0,
					"REAL" => 0.0,
					"BLOB" => Array.Empty<byte>(),
					_ => isKey ? SqliteGuid(Guid.NewGuid()) : string.Empty,
				};
			}
		}

		using var insert = connection.CreateCommand();
		var names = columns.Keys.ToList();
		insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(name => $"\"{name}\""))}) VALUES ({string.Join(", ", names.Select((_, index) => $"$p{index}"))})";
		for (var index = 0; index < names.Count; index++)
		{
			var parameter = insert.CreateParameter();
			parameter.ParameterName = $"$p{index}";
			parameter.Value = columns[names[index]];
			insert.Parameters.Add(parameter);
		}

		insert.ExecuteNonQuery();
	}
}
