using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Figma
{
    using Internals;

    internal abstract class Api : IDisposable
    {
        #region Fields
        protected readonly string fileKey;
        protected readonly HttpClient httpClient;

        const int maxRetries = 3;
        const int baseRetryDelayMs = 5000;
        const int maxRetryDelayMs = 30000;
        #endregion

        #region Constructors
        protected Api(string personalAccessToken, string fileKey)
        {
            this.fileKey = fileKey;
            httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            httpClient.DefaultRequestHeaders.Add("X-FIGMA-TOKEN", personalAccessToken);
        }
        #endregion

        #region Methods
        void IDisposable.Dispose() => httpClient.Dispose();
        #endregion

        #region Support Methods
        protected async Task<T> ConvertOnBackgroundAsync<T>(string json, CancellationToken token) where T : class => await Task.Run(() => Task.FromResult(JsonUtility.FromJson<T>(json)), token);
        protected async Task<T> GetAsync<T>(string get, CancellationToken token = default) where T : class => await ConvertOnBackgroundAsync<T>(await GetJsonAsync(get, token), token);
        protected async Task<string> GetJsonAsync(string get, CancellationToken token = default) => await HttpGetAsync($"{Internals.Const.api}/{get}", token);
        async Task<string> HttpGetAsync(string url, CancellationToken token = default)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                HttpResponseMessage response = await httpClient.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync();

                if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == maxRetries)
                    throw new HttpRequestException($"{HttpMethod.Get} {url} {response.StatusCode}");

                int delayMs = Math.Min(baseRetryDelayMs * (1 << attempt), maxRetryDelayMs);
                // respect Retry-After but cap it — Figma can return very large values during account cooldowns
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                    delayMs = Math.Max(delayMs, Math.Min((int)retryAfter.TotalMilliseconds, maxRetryDelayMs));

                await Task.Delay(delayMs, token);
            }

            throw new HttpRequestException($"{HttpMethod.Get} {url} exhausted retries");
        }
        #endregion
    }
}