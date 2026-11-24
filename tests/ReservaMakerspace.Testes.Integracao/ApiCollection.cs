namespace ReservaMakerspace.Testes.Integracao;

[CollectionDefinition(Nome)]
public sealed class ApiCollection : ICollectionFixture<FabricaDaApi>
{
    public const string Nome = "Api";
}
