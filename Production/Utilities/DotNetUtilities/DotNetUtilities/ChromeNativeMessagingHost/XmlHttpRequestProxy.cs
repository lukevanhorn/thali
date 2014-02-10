﻿/*
Copyright (c) Microsoft Open Technologies, Inc.
All Rights Reserved
Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the
License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0

THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED,
INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
MERCHANTABLITY OR NON-INFRINGEMENT.

See the Apache 2 License for the specific language governing permissions and limitations under the License.
*/

namespace ChromeNativeMessagingHost
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;

    using DotNetUtilities;

    using Org.BouncyCastle.Math;

    public static class XmlHttpRequestProxy
    {
        /// <summary>
        /// We support allowing the client to specify 0 for the modulus and exponent in a HttpKeyUrl, in that case
        /// we will (insecurely as hell) look up the server's key ourselves and then cache it here because otherwise
        /// every single request will end up being a double request.
        /// TODO: This thing could theoretically grow without limit and anyway it's insecure as heck, we need to fix it. Oh
        /// and if the server should change its key for any number of good reasons this will blow up. Bad. Bad. Bad.
        /// </summary>
        private static readonly Dictionary<Tuple<string, int>, BigIntegerRSAPublicKey> HttpKeyStore = 
            new Dictionary<Tuple<string, int>, BigIntegerRSAPublicKey>();

        /// <summary>
        /// When we provision the client's identity at a Thali Device Hub we record the address and the associated
        /// server's key in this list.
        /// TODO: This assumes there is just one client identity, which in a real Thali system wouldn't be true. Fix.
        /// </summary>
        private static readonly Dictionary<Tuple<string, int>, BigIntegerRSAPublicKey> ProvisionedList =
            new Dictionary<Tuple<string, int>, BigIntegerRSAPublicKey>();

        public static void MainLoop()
        {
            Stream outStream = null;

            // TODO: If one of the console methods throws an exception then will it be caught by the outer catch? And if
            // there is an exception inside the inner try then don't I have to assume that the using will close the streams
            // before the outter catch can send a message? I'm not sure about the answers and don't have time to explore
            // at the moment so I'm using a double wrapping of try's to take care of it.
            try
            {
                using (var inStream = Console.OpenStandardInput())
                using (outStream = Console.OpenStandardOutput())
                {
                    try
                    {
                        var workingDirectory = new DirectoryInfo(Environment.CurrentDirectory);
                        var clientCert = ThaliClientToDeviceHubUtilities.GetLocalClientCertificate(workingDirectory);
                        ChromeNativeHostUtilities.SynchronousRequestResponseMessageEngine<XmlHttpRequest>(
                            inStream,
                            outStream,
                            inMessage => ProcessMessage(inMessage, clientCert),
                            ProcessHostError);
                    }
                    catch (Exception e)
                    {
                        if (outStream != null)
                        {
                            ChromeNativeHostUtilities.SendMessage(ProcessHostError("oops! " + e.Message, null), outStream);
                        }                        
                    }
                }
            }
            catch (Exception e)
            {
                if (outStream != null)
                {
                    ChromeNativeHostUtilities.SendMessage(ProcessHostError("oops! " + e.Message, null), outStream);   
                }
            }
        }

        public static object ProcessMessage(XmlHttpRequest xmlHttpRequest, X509Certificate2 clientCert)
        {
            Debug.Assert(xmlHttpRequest != null && clientCert != null);
            if (xmlHttpRequest.type != XmlHttpRequest.typeValue)
            {
                throw new ApplicationException("The type of the incoming request was " + xmlHttpRequest.type + 
                    " and not " + XmlHttpRequest.typeValue + " as required.");
            }

            // Pouch gets unhappy if you give it a httpkey URL, so instead we give it a https URL
            // that is really a httpkey URL. So we check here.
            if (xmlHttpRequest.url.StartsWith("https://", StringComparison.Ordinal) == false)
            {
                throw new ApplicationException("Incoming URL had to be a httpkey URL masquerading as a https URL but instead it was: "
                    + xmlHttpRequest.url);
            }

            var testHttpKeyUrlString = HttpKeyUri.HttpKeySchemeName + xmlHttpRequest.url.Substring("https".Length);
            var httpKeyUri = HttpKeyUri.BuildHttpKeyUri(testHttpKeyUrlString);

            httpKeyUri = DiscoverRootCertIfNeeded(httpKeyUri, clientCert);

            var hostPortTuple = new Tuple<string, int>(httpKeyUri.Host, httpKeyUri.Port);

            if (ProvisionedList.ContainsKey(hostPortTuple) == false ||
                ProvisionedList[hostPortTuple].Equals(httpKeyUri.ServerPublicKey) == false)
            {
                ThaliClientToDeviceHubUtilities.ProvisionThaliClient(httpKeyUri.ServerPublicKey, httpKeyUri.Host, httpKeyUri.Port, clientCert);
                ProvisionedList[hostPortTuple] = httpKeyUri.ServerPublicKey;
            }

            var webRequest = ThaliClientToDeviceHubUtilities.CreateThaliWebRequest(httpKeyUri, clientCert);
            webRequest.Method = xmlHttpRequest.method;

            // There are multiple headers that cannot be set directly via webRequest.Headers. I only catch
            // two below that seem of some reasonable use.
            foreach (var headerNameValue in xmlHttpRequest.headers)
            {
                if (headerNameValue.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                {
                    webRequest.Accept = headerNameValue.Value;
                }
                else if (headerNameValue.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    webRequest.ContentType = headerNameValue.Value;
                }
                else
                {
                    webRequest.Headers.Add(headerNameValue.Key, headerNameValue.Value);    
                }
            }

            if (string.IsNullOrWhiteSpace(xmlHttpRequest.requestText) == false)
            {
                var bodyAsBytes = Encoding.UTF8.GetBytes(xmlHttpRequest.requestText);
                webRequest.GetRequestStream().Write(bodyAsBytes, 0, bodyAsBytes.Count());
            }

            var xmlHttpResponse = ProcessResponse(xmlHttpRequest.transactionId, webRequest);

            return xmlHttpResponse;
        }

        public static object ProcessHostError(string errorMessage, XmlHttpRequest xmlHttpRequest)
        {
            var xmlHttpResponse = new XmlHttpResponse
                                      {
                                          transactionId =
                                              xmlHttpRequest != null
                                                  ? xmlHttpRequest.transactionId
                                                  : null,
                                          status = 502,
                                          headers = null,
                                          responseText = errorMessage
                                      };
            return xmlHttpResponse;
        }

        /// <summary>
        /// TODO: This whole method is just wrong, what happens if the server at the address changes its key?!?!?!
        /// Once we have a real discovery framework this whole 0.0 mechanism needs to go away.
        /// </summary>
        /// <param name="httpKeyUri"></param>
        /// <param name="clientCert"></param>
        /// <returns></returns>
        private static HttpKeyUri DiscoverRootCertIfNeeded(HttpKeyUri httpKeyUri, X509Certificate2 clientCert)
        {
            if (httpKeyUri.ServerPublicKey.Exponent.Equals(BigInteger.Zero)
                && httpKeyUri.ServerPublicKey.Modulus.Equals(BigInteger.Zero))
            {
                var host = httpKeyUri.Host;
                var port = httpKeyUri.Port;
                var hostPortTuple = new Tuple<string, int>(host, port);
                BigIntegerRSAPublicKey serverPublicKey;

                if (HttpKeyStore.ContainsKey(hostPortTuple))
                {
                    serverPublicKey = HttpKeyStore[hostPortTuple];
                }
                else
                {
                    serverPublicKey = ThaliClientToDeviceHubUtilities.GetServersRootPublicKey(host, port, clientCert);
                    HttpKeyStore[hostPortTuple] = serverPublicKey;
                }

                var serverHttpKey = HttpKeyUri.BuildHttpKeyUri(
                    serverPublicKey,
                    host,
                    port,
                    httpKeyUri.PathWithoutPublicKey,
                    httpKeyUri.Query);
                return serverHttpKey;
            }

            return httpKeyUri;
        }

        private static XmlHttpResponse ProcessResponse(string transactionId, HttpWebRequest webRequest)
        {
            Debug.Assert(string.IsNullOrWhiteSpace(transactionId) == false && webRequest != null);
            HttpWebResponse webResponse;

            try
            {
                webResponse = (HttpWebResponse)webRequest.GetResponse();
            }
            catch (WebException webException)
            {
                webResponse = (HttpWebResponse)webException.Response;
            }

            var xmlHttpResponse = new XmlHttpResponse
                                      {
                                          transactionId = transactionId,
                                          status = (int)webResponse.StatusCode
                                      };

            foreach (var headerName in webResponse.Headers.AllKeys)
            {
                var lowerHeaderName = headerName.ToLowerInvariant();
                if (xmlHttpResponse.headers.ContainsKey(lowerHeaderName))
                {
                    xmlHttpResponse.headers[lowerHeaderName] = xmlHttpResponse.headers[lowerHeaderName] + ","
                                                          + webResponse.Headers[headerName];
                }
                else
                {
                    xmlHttpResponse.headers[lowerHeaderName] = webResponse.Headers[headerName];
                }
            }

            xmlHttpResponse.responseText = string.Empty;
            var readBuffer = new byte[102400];
            using (var memoryStream = new MemoryStream())
            {
                var responseStream = webResponse.GetResponseStream();

                if (responseStream != null)
                {
                    int bytesRead = responseStream.Read(readBuffer, 0, readBuffer.Count());

                    while (bytesRead > 0)
                    {
                        memoryStream.Write(readBuffer, 0, bytesRead);
                        bytesRead = responseStream.Read(readBuffer, 0, readBuffer.Count());
                    }

                    if (memoryStream.Length > 0)
                    {
                        xmlHttpResponse.responseText = Encoding.UTF8.GetString(memoryStream.ToArray());
                    }
                }
            }

            return xmlHttpResponse;
        }
    }
}
