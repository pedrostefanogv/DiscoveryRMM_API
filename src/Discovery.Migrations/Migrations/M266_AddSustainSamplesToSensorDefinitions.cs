using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Histerese de escalação: quantas amostras ruins consecutivas são exigidas
/// para subir a severidade de um sensor que JÁ está falhando (Warning →
/// Failing). Default 1 = comportamento anterior (escala imediato).
/// </summary>
[Migration(20270101_266)]
public class M266_AddSustainSamplesToSensorDefinitions : Migration
{
    private const string Table = "sensor_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Column("sustain_samples").Exists())
        {
            Alter.Table(Table)
                .AddColumn("sustain_samples")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(1);
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column("sustain_samples").Exists())
        {
            Delete.Column("sustain_samples").FromTable(Table);
        }
    }
}
