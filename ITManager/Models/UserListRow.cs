namespace ITManager.Models
{
    public sealed class UserListRow
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string GivenName { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string EmailAddress { get; set; } = string.Empty;
        public string TelephoneNumber { get; set; } = string.Empty;
        public string Mobile { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool? IsActive { get; set; }
        public bool? IsOperator { get; set; }
        public int? RoleId { get; set; }

        // NEW: account type (dbo.account_types)
        public int? AccountTypeId { get; set; }
        public string AccountTypeName { get; set; } = string.Empty;
        public string AccountTypeCode { get; set; } = string.Empty;
    }
}
