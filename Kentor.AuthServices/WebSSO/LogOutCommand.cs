﻿using System;
using Kentor.AuthServices.Configuration;
using System.IdentityModel.Metadata;
using System.Security.Claims;
using System.Net;
using Kentor.AuthServices.Saml2P;
using Kentor.AuthServices.Exceptions;
using System.Globalization;
using System.Configuration;
using System.Linq;

namespace Kentor.AuthServices.WebSso
{
    /// <summary>
    /// The logout command. Use 
    /// CommandFactory.Get(CommandFactory.LogoutCommandName) to get an instance.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "Logout")]
    public class LogoutCommand : ICommand
    {
        /// <summary>
        /// Ctor, don't want anyone to create instances.
        /// </summary>
        internal LogoutCommand() { }

        /// <summary>
        /// Run the command, initiating or handling the logout sequence.
        /// </summary>
        /// <param name="request">Request data.</param>
        /// <param name="options">Options</param>
        /// <returns>CommandResult</returns>
        public CommandResult Run(HttpRequestData request, IOptions options)
        {
            if(request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var returnUrl = request.QueryString["ReturnUrl"].SingleOrDefault();

            return Run(request, returnUrl, options);
        }

        /// <summary>
        /// Run the command, initating or handling the logout sequence.
        /// </summary>
        /// <param name="request">Request data.</param>
        /// <param name="returnPath">Path to return to, only used if this
        /// is the start of an SP-initiated logout.</param>
        /// <param name="options">Options</param>
        /// <returns>CommandResult</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2234:PassSystemUriObjectsInsteadOfStrings")]
        public static CommandResult Run(
            HttpRequestData request,
            string returnPath,
            IOptions options)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var binding = Saml2Binding.Get(request);
            if (binding != null)
            {
                var unbindResult = binding.Unbind(request, options);
                switch (unbindResult.Data.LocalName)
                {
                    case "LogoutRequest":
                        return HandleRequest(unbindResult, options);
                    case "LogoutResponse":
                        return HandleResponse(unbindResult, request, options);
                    default:
                        throw new NotImplementedException();
                }
            }

            var idpEntityId = new EntityId(
                ClaimsPrincipal.Current.FindFirst(AuthServicesClaimTypes.LogoutNameIdentifier)?.Issuer
                ?? ClaimsPrincipal.Current.FindFirst(ClaimTypes.NameIdentifier).Issuer);

            var idp = options.IdentityProviders[idpEntityId];

            var logoutRequest = idp.CreateLogoutRequest();

            var commandResult = Saml2Binding.Get(Saml2BindingType.HttpRedirect)
                .Bind(logoutRequest);
            commandResult.TerminateLocalSession = true;

            var urls = new AuthServicesUrls(request, options.SPOptions);

            if(!string.IsNullOrEmpty(returnPath))
            {
                commandResult.SetCookieData = new Uri(urls.ApplicationUrl, returnPath).ToString();
            }
            else
            {
                commandResult.SetCookieData = urls.ApplicationUrl.ToString();
            }
            commandResult.SetCookieName = "Kentor." + logoutRequest.RelayState;

            return commandResult;
        }

        private static CommandResult HandleRequest(UnbindResult unbindResult, IOptions options)
        {
            var request = Saml2LogoutRequest.FromXml(unbindResult.Data);

            var idp = options.IdentityProviders[request.Issuer];

            if(options.SPOptions.SigningServiceCertificate == null)
            {
                throw new ConfigurationErrorsException(string.Format(CultureInfo.InvariantCulture,
                    "Received a Single Logout request from \"{0}\" but cannot reply because single logout responses must be signed and there is no signing certificate configured. Looks like the idp is configured for Single Logout despite AuthServices not exposing that functionality in the metadata.",
                    request.Issuer.Id));
            }

            var response = new Saml2LogoutResponse(Saml2StatusCode.Success)
            {
                DestinationUrl = idp.SingleLogoutServiceResponseUrl,
                SigningCertificate = options.SPOptions.SigningServiceCertificate,
                InResponseTo = request.Id,
                Issuer = options.SPOptions.EntityId,
            };

            var result = Saml2Binding.Get(idp.SingleLogoutServiceBinding).Bind(response);
            result.TerminateLocalSession = true;
            return result;
        }

        private static CommandResult HandleResponse(UnbindResult unbindResult, HttpRequestData request, IOptions options)
        {
            var status = Saml2LogoutResponse.FromXml(unbindResult.Data).Status;
            if(status != Saml2StatusCode.Success)
            {
                throw new UnsuccessfulSamlOperationException(string.Format(CultureInfo.InvariantCulture,
                    "Idp returned status \"{0}\", indicating that the single logout failed. The local session has been successfully terminated.",
                    status));
            }

            return new CommandResult()
            {
                HttpStatusCode = HttpStatusCode.SeeOther,
                Location = new AuthServicesUrls(request, options.SPOptions).ApplicationUrl
            };
        }
    }
}
