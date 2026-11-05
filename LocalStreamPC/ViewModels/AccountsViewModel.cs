using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LocalStreamPC.Models;
using LocalStreamPC.Services;

namespace LocalStreamPC.ViewModels
{
    public sealed class AccountRow
    {
        public required string Username { get; init; }
        public required bool IsAdmin { get; init; }
        public required string Summary { get; init; }
        public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
        public string RoleColor => IsAdmin ? "#3B82F6" : "#10B981";
    }

    public sealed class FolderChoice : ObservableObject
    {
        public required string Path { get; init; }
        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set => SetField(ref _isChecked, value); }
    }

    /// <summary>Settings → Web accounts: who can log in to the web page, as admin or viewer, and which folders each sees.</summary>
    public sealed class AccountsViewModel : ObservableObject
    {
        private readonly ConfigService _config;
        private readonly AppLog _log;
        private string? _editingName; // null while adding a new account

        public AccountsViewModel(ConfigService config, AppLog log)
        {
            _config = config;
            _log = log;
            Reload();
        }

        public ObservableCollection<AccountRow> Accounts { get; } = new();
        public ObservableCollection<FolderChoice> EditFolders { get; } = new();

        private bool _isEditing;
        public bool IsEditing { get => _isEditing; private set { if (SetField(ref _isEditing, value)) OnPropertyChanged(nameof(IsNotEditing)); } }
        public bool IsNotEditing => !IsEditing;

        private string _editTitle = "";
        public string EditTitle { get => _editTitle; private set => SetField(ref _editTitle, value); }

        private string _editUsername = "";
        public string EditUsername { get => _editUsername; set => SetField(ref _editUsername, value); }

        private string _editPassword = "";
        public string EditPassword { get => _editPassword; set => SetField(ref _editPassword, value); }

        public string PasswordHint => _editingName == null ? "At least 6 characters" : "Blank = keep current";

        private bool _editIsAdmin;
        public bool EditIsAdmin { get => _editIsAdmin; set => SetField(ref _editIsAdmin, value); }

        private bool _editCanUpload;
        /// <summary>Viewer who may also upload files and make folders (never delete or move).</summary>
        public bool EditCanUpload { get => _editCanUpload; set => SetField(ref _editCanUpload, value); }

        private bool _editCanShare;
        /// <summary>May make expiring share links for people without an account.</summary>
        public bool EditCanShare { get => _editCanShare; set => SetField(ref _editCanShare, value); }

        private bool _editAllFolders = true;
        public bool EditAllFolders
        {
            get => _editAllFolders;
            set { if (SetField(ref _editAllFolders, value)) OnPropertyChanged(nameof(ShowFolderList)); }
        }
        public bool ShowFolderList => !EditAllFolders;

        private string? _error;
        public string? Error { get => _error; private set { if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => !string.IsNullOrEmpty(_error);

        public void Reload()
        {
            Accounts.Clear();
            foreach (var u in _config.Current.Users)
            {
                string folders = u.AllFolders ? "all shared folders"
                    : u.Folders.Count == 0 ? "no folders" : u.Folders.Count == 1 ? System.IO.Path.GetFileName(u.Folders[0].TrimEnd('\\', '/')) is { Length: > 0 } n ? n : u.Folders[0] : $"{u.Folders.Count} folders";
                Accounts.Add(new AccountRow
                {
                    Username = u.Username,
                    IsAdmin = u.IsAdmin,
                    Summary = (u.IsAdmin ? "Admin: can upload, move, delete and share"
                               : "Viewer: can browse and play" + (u.CanUpload ? ", upload" : "") + (u.CanShare ? ", share links" : "")) + " · " + folders,
                });
            }
        }

        public void BeginAdd()
        {
            _editingName = null;
            EditTitle = "New account";
            EditUsername = "";
            EditPassword = "";
            EditIsAdmin = false;
            EditCanUpload = false;
            EditCanShare = false;
            EditAllFolders = true;
            FillFolders(Array.Empty<string>());
            Error = null;
            IsEditing = true;
            OnPropertyChanged(nameof(PasswordHint));
        }

        public void BeginEdit(string username)
        {
            var user = _config.Current.Users.FirstOrDefault(u => u.Username == username);
            if (user == null) return;
            _editingName = user.Username;
            EditTitle = $"Edit \"{user.Username}\"";
            EditUsername = user.Username;
            EditPassword = "";
            EditIsAdmin = user.IsAdmin;
            EditCanUpload = user.CanUpload;
            EditCanShare = user.CanShare;
            EditAllFolders = user.AllFolders;
            FillFolders(user.Folders);
            Error = null;
            IsEditing = true;
            OnPropertyChanged(nameof(PasswordHint));
        }

        private void FillFolders(IReadOnlyCollection<string> selected)
        {
            EditFolders.Clear();
            foreach (var f in _config.Current.SharedFolders)
                EditFolders.Add(new FolderChoice { Path = f, IsChecked = selected.Contains(f, StringComparer.OrdinalIgnoreCase) });
        }

        public void CancelEdit()
        {
            IsEditing = false;
            Error = null;
        }

        /// <returns>True when saved.</returns>
        public bool SaveEdit()
        {
            string name = EditUsername.Trim();
            var users = _config.Current.Users;
            var existing = _editingName == null ? null : users.FirstOrDefault(u => u.Username == _editingName);

            if (name.Length == 0) return Fail("Enter a username.");
            if (name.Contains(':')) return Fail("Usernames can't contain ':'.");
            if (name.Length > 64) return Fail("That username is too long.");
            if (users.Any(u => u != existing && u.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) return Fail($"There is already an account called \"{name}\".");
            if (existing == null && EditPassword.Length < 6) return Fail("New accounts need a password of at least 6 characters.");
            if (EditPassword.Length is > 0 and < 6) return Fail("Passwords need at least 6 characters.");
            if (existing is { IsAdmin: true } && !EditIsAdmin && users.Count(u => u.IsAdmin) == 1) return Fail("This is the only admin. Make another account admin first.");
            var folders = EditFolders.Where(f => f.IsChecked).Select(f => f.Path).ToList();
            if (!EditAllFolders && folders.Count == 0) return Fail("Pick at least one folder, or choose \"All shared folders\".");

            var user = existing ?? new UserAccount();
            user.Username = name;
            user.IsAdmin = EditIsAdmin;
            user.CanUpload = EditCanUpload && !EditIsAdmin;
            user.CanShare = EditCanShare && !EditIsAdmin;
            user.AllFolders = EditAllFolders;
            user.Folders = EditAllFolders ? new List<string>() : folders;
            if (EditPassword.Length > 0) user.PasswordHash = ConfigService.HashPassword(EditPassword);
            if (existing == null) users.Add(user);
            _config.Save();
            _log.Write(existing == null ? $"Account \"{name}\" added." : $"Account \"{name}\" updated.");

            EditPassword = "";
            IsEditing = false;
            Error = null;
            Reload();
            return true;
        }

        /// <returns>An error to show, or null when removed.</returns>
        public string? Remove(string username)
        {
            var users = _config.Current.Users;
            var user = users.FirstOrDefault(u => u.Username == username);
            if (user == null) return null;
            if (user.IsAdmin && users.Count(u => u.IsAdmin) == 1) return "You can't remove the only admin account.";
            users.Remove(user);
            _config.Save();
            _log.Write($"Account \"{username}\" removed.");
            if (_editingName == username) CancelEdit();
            Reload();
            return null;
        }

        private bool Fail(string message)
        {
            Error = message;
            return false;
        }
    }
}
