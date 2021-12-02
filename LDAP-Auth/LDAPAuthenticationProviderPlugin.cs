using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace Jellyfin.Plugin.LDAP_Auth
{
    /// <summary>
    /// Ldap Authentication Provider Plugin.
    /// </summary>
    public class LdapAuthenticationProviderPlugin : IAuthenticationProvider
    {
        private readonly ILogger<LdapAuthenticationProviderPlugin> _logger;
        private readonly IApplicationHost _applicationHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="LdapAuthenticationProviderPlugin"/> class.
        /// </summary>
        /// <param name="applicationHost">Instance of the <see cref="IApplicationHost"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{LdapAuthenticationProviderPlugin}"/> interface.</param>
        public LdapAuthenticationProviderPlugin(IApplicationHost applicationHost, ILogger<LdapAuthenticationProviderPlugin> logger)
        {
            _logger = logger;
            _applicationHost = applicationHost;
        }

        private string[] LdapUsernameAttributes => LdapPlugin.Instance.Configuration.LdapSearchAttributes.Replace(" ", string.Empty, StringComparison.Ordinal).Split(',');

        private string UsernameAttr => LdapPlugin.Instance.Configuration.LdapUsernameAttribute;

        private string SearchFilter => LdapPlugin.Instance.Configuration.LdapSearchFilter;

        private string AdminFilter => LdapPlugin.Instance.Configuration.LdapAdminFilter;

        /// <summary>
        /// Gets plugin name.
        /// </summary>
        public string Name => "LDAP-Authentication";

        /// <summary>
        /// Gets a value indicating whether gets plugin enabled.
        /// </summary>
        public bool IsEnabled => true;

        /// <summary>
        /// Authenticate user against the ldap server.
        /// </summary>
        /// <param name="username">Username to authenticate.</param>
        /// <param name="password">Password to authenticate.</param>
        /// <returns>A <see cref="ProviderAuthenticationResult"/> with the authentication result.</returns>
        /// <exception cref="AuthenticationException">Exception when failing to authenticate.</exception>
        public async Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            var userManager = _applicationHost.Resolve<IUserManager>();
            User user = null;
            var ldapUser = LocateLdapUser(username);
            if (ldapUser == null)
            {
                _logger.LogError("Found no users matching {Username} in LDAP search", username);
                throw new AuthenticationException("Found no LDAP users matching provided username.");
            }

            var ldapUsername = GetAttribute(ldapUser, UsernameAttr)?.StringValue;
            _logger.LogDebug("Setting username: {LdapUsername}", ldapUsername);

            try
            {
                user = userManager.GetUserByName(ldapUsername);
            }
            catch (Exception e)
            {
                _logger.LogWarning("User Manager could not find a user for LDAP User, this may not be fatal", e);
            }

            using var ldapClient = ConnectToLdap(ldapUser.Dn, password);

            if (!ldapClient.Bound)
            {
                _logger.LogError("Error logging in, invalid LDAP username or password");
                throw new AuthenticationException("Error completing LDAP login. Invalid username or password.");
            }

            // Determine if the user should be an administrator
            var ldapIsAdmin = false;

            if (!string.IsNullOrEmpty(AdminFilter) && !string.Equals(AdminFilter, "_disabled_", StringComparison.Ordinal))
            {
                // Automatically follow referrals
                ldapClient.Constraints = GetSearchConstraints(
                    ldapClient,
                    ldapUser.Dn,
                    password);

                // Search the current user DN with the adminFilter
                var ldapUsers = ldapClient.Search(
                    ldapUser.Dn,
                    LdapConnection.ScopeBase,
                    AdminFilter,
                    LdapUsernameAttributes,
                    false);

                // If we got non-zero, then the filter matched and the user is an admin
                if (ldapUsers.HasMore())
                {
                    ldapIsAdmin = true;
                }
            }

            if (user == null)
            {
                _logger.LogDebug("Creating new user {Username} - is admin? {IsAdmin}", ldapUsername, ldapIsAdmin);
                if (LdapPlugin.Instance.Configuration.CreateUsersFromLdap)
                {
                    user = await userManager.CreateUserAsync(ldapUsername).ConfigureAwait(false);
                    user.AuthenticationProviderId = GetType().FullName;
                    user.SetPermission(PermissionKind.IsAdministrator, ldapIsAdmin);
                    user.SetPermission(PermissionKind.EnableAllFolders, LdapPlugin.Instance.Configuration.EnableAllFolders);
                    if (!LdapPlugin.Instance.Configuration.EnableAllFolders)
                    {
                        user.SetPreference(PreferenceKind.EnabledFolders, LdapPlugin.Instance.Configuration.EnabledFolders);
                    }

                    await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogError("User not configured for LDAP Uid: {LdapUsername}", ldapUsername);
                    throw new AuthenticationException(
                        $"Automatic User Creation is disabled and there is no Jellyfin user for authorized Uid: {ldapUsername}");
                }
            }
            else
            {
                // User exists; if the admin has enabled an AdminFilter, check if the user's
                // 'IsAdministrator' matches the LDAP configuration and update if there is a difference.
                if (!string.IsNullOrEmpty(AdminFilter) && !string.Equals(AdminFilter, "_disabled_", StringComparison.Ordinal))
                {
                    var isJellyfinAdmin = user.HasPermission(PermissionKind.IsAdministrator);
                    if (isJellyfinAdmin != ldapIsAdmin)
                    {
                        _logger.LogDebug("Updating user {Username} admin status to: {LdapIsAdmin}.", ldapUsername, ldapIsAdmin);
                        user.SetPermission(PermissionKind.IsAdministrator, ldapIsAdmin);
                        await userManager.UpdateUserAsync(user).ConfigureAwait(false);
                    }
                }
            }

            return new ProviderAuthenticationResult { Username = ldapUsername };
        }

        /// <inheritdoc />
        public bool HasPassword(User user)
        {
            return true;
        }

        /// <inheritdoc />
        public Task ChangePassword(User user, string newPassword)
        {
            throw new NotImplementedException();
        }

        private static bool LdapClient_UserDefinedServerCertValidationDelegate(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            System.Security.Cryptography.X509Certificates.X509Chain chain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors)
            => true;

        /// <summary>
        /// Returns the user search results for the provided filter.
        /// </summary>
        /// <param name="filter">The LDAP filter to search on.</param>
        /// <returns>The user DNs from the search results.</returns>
        /// <exception cref="AuthenticationException">Thrown on failure to connect or bind to LDAP server.</exception>
        public IEnumerable<string> GetFilteredUsers(string filter)
        {
            using var ldapClient = ConnectToLdap();

            ldapClient.Constraints = GetSearchConstraints(
                ldapClient,
                LdapPlugin.Instance.Configuration.LdapBindUser,
                LdapPlugin.Instance.Configuration.LdapBindPassword);

            var ldapUsers = ldapClient.Search(
                LdapPlugin.Instance.Configuration.LdapBaseDn,
                LdapConnection.ScopeSub,
                filter,
                LdapUsernameAttributes,
                false);

            // ToList to ensure enumeration is complete before the connection is closed
            return ldapUsers.Select(u => u.Dn).ToList();
        }

        /// <summary>
        /// Attempts to locate the requested username on the ldap using the plugin-configured search and attribute settings.
        /// </summary>
        /// <param name="username">The username to search.</param>
        /// <returns>The located user or null if not found.</returns>
        /// <exception cref="AuthenticationException">Thrown on failure to connect or bind to LDAP server.</exception>
        public LdapEntry LocateLdapUser(string username)
        {
            var foundUser = false;
            LdapEntry ldapUser = null;
            using var ldapClient = ConnectToLdap();

            if (!ldapClient.Connected)
            {
                return null;
            }

            ldapClient.Constraints = GetSearchConstraints(
                ldapClient,
                LdapPlugin.Instance.Configuration.LdapBindUser,
                LdapPlugin.Instance.Configuration.LdapBindPassword);

            var ldapUsers = ldapClient.Search(
                LdapPlugin.Instance.Configuration.LdapBaseDn,
                LdapConnection.ScopeSub,
                SearchFilter,
                LdapUsernameAttributes,
                false);

            _logger.LogDebug("Search: {BaseDn} {SearchFilter} @ {LdapServer}", LdapPlugin.Instance.Configuration.LdapBaseDn, SearchFilter, LdapPlugin.Instance.Configuration.LdapServer);

            var usernameComparison = LdapPlugin.Instance.Configuration.EnableCaseInsensitiveUsername
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            while (ldapUsers.HasMore() && foundUser == false)
            {
                var currentUser = ldapUsers.Next();
                foreach (var attr in LdapUsernameAttributes)
                {
                    var toCheck = GetAttribute(currentUser, attr);
                    if (toCheck?.StringValueArray != null)
                    {
                        foreach (var name in toCheck.StringValueArray)
                        {
                            if (string.Equals(username, name, usernameComparison))
                            {
                                ldapUser = currentUser;
                                foundUser = true;
                                break;
                            }
                        }
                    }
                }
            }

            return ldapUser;
        }

        private LdapAttribute GetAttribute(LdapEntry userEntry, string attr)
        {
            var attributeSet = userEntry.GetAttributeSet();
            if (attributeSet.ContainsKey(attr))
            {
                return attributeSet.GetAttribute(attr);
            }

            _logger.LogWarning("LDAP attribute {attr} not found for user {user}", attr, userEntry.Dn);
            return null;
        }

        private static LdapConnectionOptions GetConnectionOptions()
        {
            var connectionOptions = new LdapConnectionOptions();
            var configuration = LdapPlugin.Instance.Configuration;
            if (configuration.UseSsl)
            {
                connectionOptions.UseSsl();
            }

            if (configuration.SkipSslVerify)
            {
                connectionOptions.ConfigureRemoteCertificateValidationCallback(LdapClient_UserDefinedServerCertValidationDelegate);
            }

            return connectionOptions;
        }

        private LdapSearchConstraints GetSearchConstraints(
            LdapConnection ldapClient, string dn, string password)
        {
            var constraints = ldapClient.SearchConstraints;
            constraints.ReferralFollowing = true;
            constraints.setReferralHandler(new LdapAuthHandler(_logger, dn, password));
            return constraints;
        }

        private LdapConnection ConnectToLdap(string userDn = null, string userPassword = null)
        {
            bool initialConnection = userDn == null;
            if (initialConnection)
            {
                userDn = LdapPlugin.Instance.Configuration.LdapBindUser;
                userPassword = LdapPlugin.Instance.Configuration.LdapBindPassword;
            }

            // not using `using` for the ability to return ldapClient, need to dispose this manually on exception
            var ldapClient = new LdapConnection(GetConnectionOptions());
            try
            {
                ldapClient.Connect(LdapPlugin.Instance.Configuration.LdapServer, LdapPlugin.Instance.Configuration.LdapPort);
                if (LdapPlugin.Instance.Configuration.UseStartTls)
                {
                    ldapClient.StartTls();
                }

                _logger.LogDebug("Trying bind as user {userDn}", userDn);
                ldapClient.Bind(userDn, userPassword);
            }
            catch (Exception e)
            {
                ldapClient.Dispose();

                _logger.LogError(e, "Failed to Connect or Bind to server as user {UserDn}", userDn);
                var message = initialConnection
                    ? "Failed to Connect or Bind to server."
                    : "Error completing LDAP login. Invalid username or password.";
                throw new AuthenticationException(message);
            }

            return ldapClient;
        }

        /// <summary>
        /// Tests the server connection and bind settings.
        /// </summary>
        /// <returns>A string reporting the result of the sequence of connection steps.</returns>
        public string TestServerBind()
        {
            var configuration = LdapPlugin.Instance.Configuration;
            var connectionOptions = GetConnectionOptions();
            var response = new StringBuilder();

            try
            {
                response.Append("Connect (");
                using var ldapClient = new LdapConnection(connectionOptions);
                ldapClient.Connect(configuration.LdapServer, configuration.LdapPort);
                response.Append("Success)");

                if (configuration.UseStartTls)
                {
                    response.Append("; Set StartTLS (");
                    ldapClient.StartTls();
                    response.Append("Success)");
                }

                response.Append("; Bind (");
                ldapClient.Bind(configuration.LdapBindUser, configuration.LdapBindPassword);
                response.Append(ldapClient.Bound ? "Success)" : "Anonymous)");

                response.Append("; Base Search (");
                var entries = ldapClient.Search(
                    configuration.LdapBaseDn,
                    LdapConnection.ScopeSub,
                    string.Empty,
                    Array.Empty<string>(),
                    false);

                // entries.Count is unreliable (timing issue?), iterate to count
                var count = 0;
                while (entries.HasMore())
                {
                    entries.Next();
                    count++;
                }

                response.Append("Found ").Append(count).Append(" Entities)");
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Ldap Test Failed to Connect or Bind to server");
                response.Append("Error: ").Append(e.Message).Append(')');
            }

            return response.ToString();
        }
    }
}
