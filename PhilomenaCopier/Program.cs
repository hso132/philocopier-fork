﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PhilomenaCopier {
    public class Program {
        public static string Version {
            get {
                Version version = typeof(Program).Assembly.GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        // Matches a domain, ignoring "http"/"https" and trailing "/"
        private const string DomainPattern = @"^(?:https?:\/\/)?(.+?\..+?)\/?$$";

		private const string InSiteLinkPattern = @">>([0-9]+)(t|p?)";
        private const string RelativeLinkPattern = @"""(.+)"":(\/.+) *";

	    private const int maxAttemptsAtMaxDelay = 2;

        // Matches a Philomena API Key. 20 characters long.
        private const string ApiKeyPattern = @"^(.{20})$";

        private const int PerPage = 50;

        private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(0.25);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(512);  // 17 minutes and 4 seconds

        // A browser user agent
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:77.0) Gecko/20100101 Firefox/77.0";

        private class Image {
            public string description { get; set; }
            public string source_url { get; set; }
            public List<string> tags { get; set; }
            public string view_url { get; set; }
            public int id { get; set; }
        }

        private class ImageTwibooru {
            public string description { get; set; }
            public string source_url { get; set; }
            public string tags { get; set; }
            public string image { get; set; }
            public int id { get; set; }
			public static implicit operator Image (ImageTwibooru d) {
				Image res = new Image();
				res.description = d.description;
				res.source_url = d.source_url;
				res.tags = new List<string>(d.tags.Split(", "));
				res.view_url = d.image;
				res.id = d.id;

				return res;
			}
        }


        private class SearchQueryImages {
            public List<Image> images { get; set; }
            public int total { get; set; }
        }

		private class SearchQueryImagesTwibooru {
			public List<ImageTwibooru> search { get; set; }
			public int total { get; set; }
		}

        private class UploadImageInfo {
            public string description { get; set; }
            public string tag_input { get; set; }
            public string source_url { get; set; }
        }

        private class UploadImageBody {
            public UploadImageInfo image { get; set; }
            public string url { get; set; }
        }

        private enum PostStatus {
            Success,
            Duplicate,
            Failure,
        }

        private static string GetSearchQueryUrl(string booru, string apiKey, string query, int page) {
			if (booru != "twibooru.org") {
            	return $"https://{booru}/api/v1/json/search/images?key={apiKey}&page={page}&per_page={PerPage}&q={query}&sf=created_at&sd=asc";
			} else {
				return $"https://{booru}/search.json?key={apiKey}&page={page}&per_page={PerPage}&q={query}&sf=created_at&sd=asc";
			}
        }

        private static string GetUploadImageUrl(string booru, string apiKey) {
            return $"https://{booru}/api/v1/json/images?key={apiKey}";
        }

        private static string GetInputWithPattern(string pattern, string promptText, string errorText = "Invalid input") {
            while (true) {
                Console.Write(promptText);
                string input = Console.ReadLine().Trim();

                // Check against pattern
                Match match = Regex.Match(input, pattern);
                if (match.Success) {
                    return match.Groups[1].Value;
                }

                Console.WriteLine(errorText);
            }
        }

		private static string ReplaceMDLink(Match match, string website) {
    		return "\">> " + match.Groups[1] + match.Groups[2] + "\":https://" + website + "/images/" + match.Groups[1] + " ";
  		}

        private static string ReplaceRelLink(Match match, string website) {
            return "\"" + match.Groups[1] + "\":https://" + website + match.Groups[2];
        }


        private static async Task<SearchQueryImages> GetSearchQueryImages(WebClient wc, string booru, string apiKey, string query, int page) {
            // Set required headers
            wc.Headers["User-Agent"] = UserAgent;

            string queryUrl = GetSearchQueryUrl(booru, apiKey, query, page);
            try {
                string searchJson = await wc.DownloadStringTaskAsync(queryUrl);
                SearchQueryImages result;
				if (booru != "twibooru.org") {
					result = JsonConvert.DeserializeObject<SearchQueryImages>(searchJson);
				} else {
					SearchQueryImagesTwibooru intermResult = JsonConvert.DeserializeObject<SearchQueryImagesTwibooru>(searchJson);
					result = new SearchQueryImages();
					result.total = intermResult.total;
					result.images = intermResult.search.ConvertAll(im => (Image)im);
				}
				if (result.images == null) {
					Console.WriteLine($"Null search images. JSON: {searchJson}");
				}
				return result;
            }
            catch (WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError) {
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null) {
                        // Other http status code
                        Console.WriteLine($"Error uploading image ({response.StatusCode})(request: {queryUrl}");
                    }
                    else {
                        // no http status code available
                        Console.WriteLine("Error uploading image (Unknown error)");
                    }
                }
                else {
                    // no http status code available
                    Console.WriteLine("Error uploading image (Unknown error)");
                }
                return null;
            }
        }

        private static async Task<PostStatus> UploadImage(WebClient wc, Image image, string booru, string apiKey) {
            // Set required headers
            wc.Headers["User-Agent"] = UserAgent;
            wc.Headers["Content-Type"] = "application/json";

            string uploadUrl = GetUploadImageUrl(booru, apiKey);

            // Format the tags into a comma-separated string
            string tagString = string.Join(", ", image.tags);

            // Create upload json
            UploadImageInfo uploadImage = new UploadImageInfo
            {
                description = image.description,
                tag_input = tagString,
                source_url = image.source_url
            };

            UploadImageBody uploadImageBody = new UploadImageBody
            {
                image = uploadImage,
                url = image.view_url
            };
            string uploadImageString = JsonConvert.SerializeObject(uploadImageBody);

            try {
                await wc.UploadDataTaskAsync(uploadUrl, Encoding.UTF8.GetBytes(uploadImageString));

                return PostStatus.Success;
            }
            catch (WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError) {
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null) {
                        if (response.StatusCode == HttpStatusCode.BadRequest) {  // Already uploaded (duplicate hash)
                            Console.WriteLine("Image has already been uploaded");
                            return PostStatus.Duplicate;
                        }
                        else {
                            // Other http status code
                            Console.WriteLine($"Error uploading image ({response.StatusCode})");
                        }
                    }
                    else {
                        // no http status code available
                        Console.WriteLine("Error uploading image (Unknown error)");
                    }
                }
                else {
                    // no http status code available
                    Console.WriteLine("Error uploading image (Unknown error)");
                }
            }

            return PostStatus.Failure;
        }

        public static async Task Main(string[] args) {
            Console.WriteLine($"Philomena Copier v{Version}");
            Console.WriteLine();
            Console.WriteLine("Ensure your filters are set correctly on the source booru. The active filter will be used when copying images.");
            Console.WriteLine("API keys can be found on the Account page.");
            Console.WriteLine();

            // Get booru info
            string sourceBooru = GetInputWithPattern(DomainPattern, "Enter source booru url: ");
            string sourceApiKey = GetInputWithPattern(ApiKeyPattern, "Enter source booru API Key: ");
            string targetBooru = GetInputWithPattern(DomainPattern, "Enter target booru url: ");
            string targetApiKey = GetInputWithPattern(ApiKeyPattern, "Enter target booru API Key: ");

            // Get query
            Console.WriteLine("Enter query to copy from the source booru to the target booru. Any query that can be made on the site will work.");
            Console.Write("Query: ");
            string searchQuery = Console.ReadLine().Trim();

            using (WebClient wc = new WebClient()) {
                // Get the first page of images
                int currentPage = 1;
                SearchQueryImages searchImages = await GetSearchQueryImages(wc, sourceBooru, sourceApiKey, searchQuery, currentPage);

                // Check how many images are in the query
                if (searchImages.total == 0) {
                    Console.WriteLine("This query has no images! Double-check the query and try again.");
                    return;
                }

                Console.WriteLine($"There are {searchImages.total} images in this query");
                Console.WriteLine("Ensure the query and image count are correct! If they are not, Ctrl-C to exit. Otherwise, press enter to continue.");
                Console.ReadLine();
				if (searchImages.images == null) {
					Console.WriteLine("Fatal error, searchImages.images is null");
				}

                // Upload all images
                int currentImage = 1;
                TimeSpan currentRetryDelay;
				string sourceBooruAbbreviated = sourceBooru.Substring(0, sourceBooru.LastIndexOf("."));
                while (searchImages.images.Count > 0) {
                    // Upload the current page
                    foreach (Image image in searchImages.images) {
                        // Reset the retry delay
                        currentRetryDelay = InitialRetryDelay;
						image.description = Regex.Replace(image.description, InSiteLinkPattern, new MatchEvaluator(match => ReplaceMDLink(match, sourceBooru)));
                        image.description = Regex.Replace(image.description, RelativeLinkPattern, new MatchEvaluator(match => ReplaceRelLink(match, sourceBooru)));
						image.tags.Add($"{sourceBooruAbbreviated} import");

                        bool success = false;
			            int attemptsAtMaxDelay = 0;

                        while (!success && attemptsAtMaxDelay < maxAttemptsAtMaxDelay) {
                            Console.WriteLine($"Uploading image {currentImage}/{searchImages.total} ({image.id})...");
                            PostStatus status = await UploadImage(wc, image, targetBooru, targetApiKey);

                            if (status == PostStatus.Failure) {
                                // Exponential backoff to prevent overloading server
                                Console.WriteLine($"Retrying in {currentRetryDelay.TotalSeconds} seconds...");
                                await Task.Delay(currentRetryDelay);

                                // Double the delay for next time, if it is below the max
                                if (currentRetryDelay < MaxRetryDelay) {
                                    currentRetryDelay *= 2;
                                } else {
				                    attemptsAtMaxDelay++;
				                }
                            } else {
                                // Move to the next image
                                success = true;
                            }
                        }

			            if (attemptsAtMaxDelay >= maxAttemptsAtMaxDelay) {
			                Console.WriteLine("Max attempts reached; moving onto next image.");
			            }

                        currentImage++;

                        // Delay to prevent overloading servers
                        await Task.Delay(InitialRetryDelay);
                    }

                    currentRetryDelay = InitialRetryDelay;
                    // Load the next page
                    currentPage++;
                    do {
                        searchImages = await GetSearchQueryImages(wc, sourceBooru, sourceApiKey, searchQuery, currentPage);
                        if (searchImages == null) {
                            Console.WriteLine($"Retrying in {currentRetryDelay.TotalSeconds} seconds...");
                            await Task.Delay(currentRetryDelay);
                            if (currentRetryDelay < MaxRetryDelay) {
                                currentRetryDelay *= 2;
                            } 
                        }
                    } while(searchImages == null);
                }
            }

            Console.WriteLine("Complete!");
        }
    }
}
