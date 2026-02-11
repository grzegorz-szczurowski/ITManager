namespace ITManager.Models
{
    /// <summary>
    /// Uniwersalny model pozycji słownikowej (Id + Name),
    /// używany w Devices, Licenses i innych modułach.
    /// </summary>
    public sealed class LookupItem
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public override string ToString() => Name;
    }
}
