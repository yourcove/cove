using Cove.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Cove.Sdk;

/// <summary>
/// Base class for extensions that contribute database tables and migrations.
/// Provides simplified migration management via the fluent <see cref="Migration"/> helper.
/// </summary>
public abstract class DataExtensionBase : CoveExtensionBase, IDataExtension
{
    private readonly List<ExtensionMigration> _migrations = [];

    /// <summary>
    /// Override to configure your entity model (tables, indexes, etc.).
    /// </summary>
    public abstract void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>
    /// Override to define migrations. Call <see cref="Migration"/> to add each migration.
    /// Migrations are applied in the order they are added. They run exactly once (tracked by name).
    /// </summary>
    protected virtual void DefineMigrations() { }

    public IReadOnlyList<ExtensionMigration> GetMigrations()
    {
        _migrations.Clear();
        DefineMigrations();
        return _migrations;
    }

    /// <summary>
    /// Adds a named SQL migration. Called inside <see cref="DefineMigrations"/>.
    /// </summary>
    /// <param name="name">Unique migration name (e.g. "001_create_audios_table"). Must be sortable.</param>
    /// <param name="sql">SQL to apply this migration.</param>
    protected void Migration(string name, string sql)
    {
        _migrations.Add(new ExtensionMigration(name, sql));
    }
}
