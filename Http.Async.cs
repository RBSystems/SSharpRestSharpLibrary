#region License

//   Copyright 2010 John Sheehan
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License. 

#endregion

using System;
using Crestron.SimplSharp;
using SSMono.Threading;
using IAsyncResult = Crestron.SimplSharp.CrestronIO.IAsyncResult;
using System.Diagnostics;
using System.Linq;
using SSMono.Net;
using SSharp.Threading;
#if ASYNC
using System.Threading.Tasks;
#endif
using RestSharp.Extensions;

namespace RestSharp
	{
	/// <summary>
	///     HttpWebRequest wrapper (async methods)
	/// </summary>
	public partial class Http
		{
		private TimeOutState timeoutState;

		public HttpWebRequest DeleteAsync (Action<HttpResponse> action)
			{
			return GetStyleMethodInternalAsync ("DELETE", action);
			}

		public HttpWebRequest GetAsync (Action<HttpResponse> action)
			{
			return GetStyleMethodInternalAsync ("GET", action);
			}

		public HttpWebRequest HeadAsync (Action<HttpResponse> action)
			{
			return GetStyleMethodInternalAsync ("HEAD", action);
			}

		public HttpWebRequest OptionsAsync (Action<HttpResponse> action)
			{
			return GetStyleMethodInternalAsync ("OPTIONS", action);
			}

		public HttpWebRequest PostAsync (Action<HttpResponse> action)
			{
			return PutPostInternalAsync ("POST", action);
			}

		public HttpWebRequest PutAsync (Action<HttpResponse> action)
			{
			return PutPostInternalAsync ("PUT", action);
			}

		public HttpWebRequest PatchAsync (Action<HttpResponse> action)
			{
			return PutPostInternalAsync ("PATCH", action);
			}

		public HttpWebRequest MergeAsync (Action<HttpResponse> action)
			{
			return PutPostInternalAsync ("MERGE", action);
			}

		/// <summary>
		///     Execute an async POST-style request with the specified HTTP Method.
		/// </summary>
		/// <param name="action"></param>
		/// <param name="httpMethod">The HTTP method to execute.</param>
		/// <returns></returns>
		public HttpWebRequest AsPostAsync (Action<HttpResponse> action, string httpMethod)
			{
			return PutPostInternalAsync (httpMethod.ToUpperInvariant (), action);
			}

		/// <summary>
		///     Execute an async GET-style request with the specified HTTP Method.
		/// </summary>
		/// <param name="action"></param>
		/// <param name="httpMethod">The HTTP method to execute.</param>
		/// <returns></returns>
		public HttpWebRequest AsGetAsync (Action<HttpResponse> action, string httpMethod)
			{
			return GetStyleMethodInternalAsync (httpMethod.ToUpperInvariant (), action);
			}

		private HttpWebRequest GetStyleMethodInternalAsync (string method, Action<HttpResponse> callback)
			{
			HttpWebRequest webRequest = null;

			try
				{
				var url = Url;

				webRequest = ConfigureAsyncWebRequest (method, url);

				if (HasBody && (method == "DELETE" || method == "OPTIONS"))
					{
					webRequest.ContentType = RequestContentType;
					WriteRequestBodyAsync (webRequest, callback);
					}
				else
					{
					//webRequest.GetResponseAsync ();

					timeoutState = new TimeOutState {Request = webRequest};

					var asyncResult = webRequest.BeginGetResponse (
						result => ResponseCallback (result, callback), webRequest);

					SetTimeout (asyncResult, timeoutState);
					}
				}
			catch (Exception ex)
				{
				ExecuteCallback (CreateErrorResponse (ex), callback);
				}

			return webRequest;
			}

		private HttpResponse CreateErrorResponse (Exception ex)
			{
			var response = new HttpResponse ();

			var webException = ex as WebException;
			if (webException != null && webException.Status == WebExceptionStatus.RequestCanceled)
				{
				response.ResponseStatus = timeoutState.TimedOut
					? ResponseStatus.TimedOut
					: ResponseStatus.Aborted;

				return response;
				}

			response.ErrorMessage = ex.Message;
			response.ErrorException = ex;
			response.ResponseStatus = ResponseStatus.Error;

			return response;
			}

		private HttpWebRequest PutPostInternalAsync (string method, Action<HttpResponse> callback)
			{
			HttpWebRequest webRequest = null;

			try
				{
				webRequest = ConfigureAsyncWebRequest (method, Url);
				PreparePostBody (webRequest);
				WriteRequestBodyAsync (webRequest, callback);
				}
			catch (Exception ex)
				{
				ExecuteCallback (CreateErrorResponse (ex), callback);
				}

			return webRequest;
			}

		private void WriteRequestBodyAsync (HttpWebRequest webRequest, Action<HttpResponse> callback)
			{
			IAsyncResult asyncResult;
			timeoutState = new TimeOutState {Request = webRequest};

			if (HasBody || HasFiles || AlwaysMultipartFormData)
				{
				webRequest.ContentLength = CalculateContentLength ();

				asyncResult = webRequest.BeginGetRequestStream (
					result => RequestStreamCallback (result, callback), webRequest);
				}
			else
				{
				asyncResult = webRequest.BeginGetResponse (r => ResponseCallback (r, callback), webRequest);
				}

			SetTimeout (asyncResult, timeoutState);
			}

		private long CalculateContentLength ()
			{
			if (RequestBodyBytes != null)
				return RequestBodyBytes.Length;

			if (!HasFiles && !AlwaysMultipartFormData)
				return Encoding.GetByteCount (RequestBody);

			// calculate length for multipart form
			long length = 0;

			foreach (var file in Files)
				{
				length += Encoding.GetByteCount (GetMultipartFileHeader (file));
				length += file.ContentLength;
				length += Encoding.GetByteCount (LineBreak);
				}

			length = Parameters.Aggregate (length,
				(current, param) => current + Encoding.GetByteCount (GetMultipartFormData (param)));

			length += Encoding.GetByteCount (GetMultipartFooter ());

			return length;
			}

		private void RequestStreamCallback (IAsyncResult result, Action<HttpResponse> callback)
			{
			var webRequest = (HttpWebRequest)result.AsyncState;

			if (timeoutState.TimedOut)
				{
				var response = new HttpResponse {ResponseStatus = ResponseStatus.TimedOut};

				ExecuteCallback (response, callback);

				return;
				}

			// write body to request stream
			try
				{
				using (var requestStream = webRequest.EndGetRequestStream (result))
					{
					if (HasFiles || AlwaysMultipartFormData)
						WriteMultipartFormData (requestStream);
					else if (RequestBodyBytes != null)
						requestStream.Write (RequestBodyBytes, 0, RequestBodyBytes.Length);
					else if (RequestBody != null)
						WriteStringTo (requestStream, RequestBody);
					}

				var asyncResult = webRequest.BeginGetResponse (r => ResponseCallback (r, callback), webRequest);

				SetTimeout (asyncResult, timeoutState);
				}
			catch (Exception ex)
				{
				ExecuteCallback (CreateErrorResponse (ex), callback);
				}
			}

		private void SetTimeout (IAsyncResult asyncResult, TimeOutState timeOutState)
			{
			if (Timeout != 0)
				ThreadPool.RegisterWaitForSingleObject (new CWaitHandle (asyncResult.AsyncWaitHandle),
					TimeoutCallback, timeOutState, Timeout, true);
			}

		private static void TimeoutCallback (object state, bool timedOut)
			{
			HttpWebRequest __CAE_HttpWebRequest__;
			if (!timedOut)
				return;

			var timeoutState = state as TimeOutState;
			if (timeoutState == null)
				return;

			lock (timeoutState)
				{
				timeoutState.TimedOut = true;
				}

			if ((__CAE_HttpWebRequest__ = timeoutState.Request) != null)
				__CAE_HttpWebRequest__.Abort ();
			}

		private static void GetRawResponseAsync (IAsyncResult result, Action<HttpWebResponse> callback)
			{
			HttpWebResponse raw;

			try
				{
				var webRequest = (HttpWebRequest)result.AsyncState;

				raw = webRequest.EndGetResponse (result) as HttpWebResponse;
				}
			catch (WebException ex)
				{
				if (ex.Status == WebExceptionStatus.RequestCanceled)
					throw;

				// Check to see if this is an HTTP error or a transport error.
				// In cases where an HTTP error occurs ( status code >= 400 )
				// return the underlying HTTP response, otherwise assume a
				// transport exception (ex: connection timeout) and
				// rethrow the exception

				var response = ex.Response as HttpWebResponse;
				if (response != null)
					raw = response;
				else
					throw;
				}

			callback (raw);

			if (raw != null)
				raw.Close ();
			}

		private void ResponseCallback (IAsyncResult result, Action<HttpResponse> callback)
			{
			try
				{
				if (timeoutState.TimedOut)
					{
					var response = new HttpResponse {ResponseStatus = ResponseStatus.TimedOut};
					ExecuteCallback (response, callback);

					return;
					}

				GetRawResponseAsync (result, webResponse =>
					{
					var response = ExtractResponseData (webResponse);
					webResponse.Dispose ();
					ExecuteCallback (response, callback);
					});
				}
			catch (Exception ex)
				{
				ExecuteCallback (CreateErrorResponse (ex), callback);
				}
			}

		private static void ExecuteCallback (HttpResponse response, Action<HttpResponse> callback)
			{
			PopulateErrorForIncompleteResponse (response);
			callback (response);
			}

		private static void PopulateErrorForIncompleteResponse (IHttpResponse response)
			{
			if (response.ResponseStatus != ResponseStatus.Completed && response.ErrorException == null)
				{
				response.ErrorException = response.ResponseStatus.ToWebException ();
				response.ErrorMessage = response.ErrorException.Message;
				}
			}

		[Obsolete ("Use the WebRequestConfigurator delegate instead of overriding this method")]
		protected virtual HttpWebRequest ConfigureAsyncWebRequest (string method, Uri url)
			{
			return ConfigureWebRequest (method, url);
			}

		private class TimeOutState
			{
			public bool TimedOut { get; set; }

			public HttpWebRequest Request { get; set; }
			}
		}
	}