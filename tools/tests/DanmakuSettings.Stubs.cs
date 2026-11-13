namespace Windows.Storage
{
    public sealed class ApplicationDataCompositeValue : System.Collections.Generic.Dictionary<string, object> { }
    public sealed class ApplicationDataContainer
    {
        public System.Collections.Generic.IDictionary<string, object> Values { get; } =
            new System.Collections.Generic.Dictionary<string, object>();
    }
    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public ApplicationDataContainer LocalSettings { get; } = new ApplicationDataContainer();
    }
}
