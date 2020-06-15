using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WebApplication.Definitions;
using WebApplication.Services;
using WebApplication.State;
using WebApplication.Utilities;

namespace WebApplication.Processing
{
    /// <summary>
    /// Class to place generated data files to expected places.
    /// </summary>
    public class Arranger
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly UserResolver _userResolver;

        // generate unique names for files. The files will be moved to correct places after hash generation.
        public readonly string Parameters = $"{Guid.NewGuid():N}.json";
        public readonly string Thumbnail = $"{Guid.NewGuid():N}.png";
        public readonly string SVF = $"{Guid.NewGuid():N}.zip";
        public readonly string InputParams = $"{Guid.NewGuid():N}.json";
        public readonly string OutputModel = $"{Guid.NewGuid():N}.zip";
        public readonly string OutputSAT = $"{Guid.NewGuid():N}.sat";
        public readonly string OutputRFA = $"{Guid.NewGuid():N}.rfa";

        /// <summary>
        /// Constructor.
        /// </summary>
        public Arranger(IHttpClientFactory clientFactory, UserResolver userResolver)
        {
            _clientFactory = clientFactory;
            _userResolver = userResolver;
        }

        /// <summary>
        /// Create adoption data.
        /// </summary>
        /// <param name="docUrl">URL to the input Inventor document (IPT or zipped IAM)</param>
        /// <param name="tlaFilename">Top level assembly in the ZIP. (if any)</param>
        public async Task<AdoptionData> ForAdoptionAsync(string docUrl, string tlaFilename)
        {
            var bucket = await _userResolver.GetBucket();

            var urls = await Task.WhenAll(bucket.CreateSignedUrlAsync(Thumbnail, ObjectAccess.Write), 
                                            bucket.CreateSignedUrlAsync(SVF, ObjectAccess.Write), 
                                            bucket.CreateSignedUrlAsync(Parameters, ObjectAccess.Write),
                                            bucket.CreateSignedUrlAsync(OutputModel, ObjectAccess.Write));

            return new AdoptionData
            {
                InputDocUrl       = docUrl,
                ThumbnailUrl      = urls[0],
                SvfUrl            = urls[1],
                ParametersJsonUrl = urls[2],
                OutputModelUrl    = urls[3],
                TLA               = tlaFilename
            };
        }

        /// <summary>
        /// Create adoption data.
        /// </summary>
        /// <param name="docUrl">URL to the input Inventor document (IPT or zipped IAM)</param>
        /// <param name="tlaFilename">Top level assembly in the ZIP. (if any)</param>
        /// <param name="parameters">Inventor parameters.</param>
        public async Task<UpdateData> ForUpdateAsync(string docUrl, string tlaFilename, InventorParameters parameters)
        {
            var bucket = await _userResolver.GetBucket();

            var urls = await Task.WhenAll(
                                            bucket.CreateSignedUrlAsync(OutputModel, ObjectAccess.Write),
                                            bucket.CreateSignedUrlAsync(SVF, ObjectAccess.Write),
                                            bucket.CreateSignedUrlAsync(Parameters, ObjectAccess.Write),
                                            bucket.CreateSignedUrlAsync(InputParams, ObjectAccess.ReadWrite));

            await using var jsonStream = Json.ToStream(parameters);
            await bucket.UploadObjectAsync(InputParams, jsonStream);

            return new UpdateData
            {
                InputDocUrl       = docUrl,
                OutputModelUrl    = urls[0],
                SvfUrl            = urls[1],
                ParametersJsonUrl = urls[2],
                InputParamsUrl    = urls[3],
                TLA               = tlaFilename
            };
        }

        /// <summary>
        /// Move project OSS objects to correct places.
        /// NOTE: it's expected that the data is generated already.
        /// </summary>
        /// <returns>Parameters hash.</returns>
        public async Task<string> MoveProjectAsync(Project project, string tlaFilename)
        {
            var hashString = await GenerateParametersHashAsync();
            var attributes = new ProjectMetadata { Hash = hashString, TLA = tlaFilename };

            var ossNames = project.OssNameProvider(hashString);

            var bucket = await _userResolver.GetBucket();

            // move data to expected places
            await Task.WhenAll(bucket.RenameObjectAsync(Thumbnail, project.OssAttributes.Thumbnail),
                                bucket.RenameObjectAsync(SVF, ossNames.ModelView),
                                bucket.RenameObjectAsync(Parameters, ossNames.Parameters),
                                bucket.RenameObjectAsync(OutputModel, ossNames.CurrentModel),
                                bucket.UploadObjectAsync(project.OssAttributes.Metadata, Json.ToStream(attributes, writeIndented: true)));

            return hashString;
        }

        /// <summary>
        /// Move temporary OSS files to the correct places.
        /// </summary>
        internal async Task MoveRfaAsync(Project project, string hash)
        {
            var bucket = await _userResolver.GetBucket();

            var ossNames = project.OssNameProvider(hash);
            await Task.WhenAll(bucket.RenameObjectAsync(OutputRFA, ossNames.Rfa),
                                bucket.DeleteAsync(OutputSAT));
        }

        internal async Task<ProcessingArgs> ForSatAsync(string inputDocUrl, string topLevelAssembly)
        {
            var bucket = await _userResolver.GetBucket();

            // SAT file is intermediate and will be used later for further conversion (to RFA),
            // so request both read and write access to avoid extra calls to OSS
            var satUrl = await bucket.CreateSignedUrlAsync(OutputSAT, ObjectAccess.ReadWrite);

            return new ProcessingArgs
            {
                InputDocUrl = inputDocUrl,
                TLA = topLevelAssembly,
                SatUrl = satUrl
            };
        }

        internal async Task<ProcessingArgs> ForRfaAsync(string inputDocUrl)
        {
            var bucket = await _userResolver.GetBucket();
            var rfaUrl = await bucket.CreateSignedUrlAsync(OutputRFA, ObjectAccess.Write);

            return new ProcessingArgs
            {
                InputDocUrl = inputDocUrl,
                RfaUrl = rfaUrl
            };
        }

        /// <summary>
        /// Move viewables OSS objects to correct places.
        /// NOTE: it's expected that the data is generated already.
        /// </summary>
        /// <returns>Parameters hash.</returns>
        public async Task<string> MoveViewablesAsync(Project project)
        {
            var hashString = await GenerateParametersHashAsync();

            var ossNames = project.OssNameProvider(hashString);

            var bucket = await _userResolver.GetBucket();

            // move data to expected places
            await Task.WhenAll(bucket.RenameObjectAsync(SVF, ossNames.ModelView),
                                bucket.RenameObjectAsync(Parameters, ossNames.Parameters),
                                bucket.RenameObjectAsync(OutputModel, ossNames.CurrentModel),
                                bucket.DeleteAsync(InputParams));

            return hashString;
        }

        /// <summary>
        /// Generate hash string for the _temporary_ parameters json.
        /// </summary>
        private async Task<string> GenerateParametersHashAsync()
        {
            var client = _clientFactory.CreateClient();

            // rearrange generated data according to the parameters hash
            var bucket = await _userResolver.GetBucket();
            var url = await bucket.CreateSignedUrlAsync(Parameters);
            using var response = await client.GetAsync(url); // TODO: find
            response.EnsureSuccessStatusCode();

            // generate hash for parameters
            var stream = await response.Content.ReadAsStreamAsync();
            var parameters = await JsonSerializer.DeserializeAsync<InventorParameters>(stream);
            return Crypto.GenerateObjectHashString(parameters);
        }
    }
}
