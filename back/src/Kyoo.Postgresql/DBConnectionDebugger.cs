using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;

namespace Kyoo.Postgresql;

public class DBConnectionDebugger : DbConnection
{
    private readonly DbConnection db;
    public DBConnectionDebugger(DbConnection db)
    {
        Console.WriteLine("Creating connection...");
        this.db = db;
    }

    public override string ConnectionString { get => db.ConnectionString; set => db.ConnectionString = value; }

    public override string Database => db.Database;

    public override string DataSource => db.DataSource;

    public override string ServerVersion => db.ServerVersion;

    public override ConnectionState State => db.State;

    public override void ChangeDatabase(string databaseName)
    {
        db.ChangeDatabase(databaseName);
    }

    public override void Close()
    {
        Console.WriteLine("Closing connection...");
        db.Close();
    }

    public virtual Task CloseAsync()
    {
        Console.WriteLine("Closing connection asynchronously...");
        return db.CloseAsync();
    }

    public virtual ValueTask DisposeAsync()
    {
        Console.WriteLine("Disposing connection asynchronously...");
        return db.DisposeAsync();
    }

    public override void Open()
    {
        Console.WriteLine("Opening connection...");
        db.Open();
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return db.BeginTransaction(isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return db.CreateCommand();
    }
}