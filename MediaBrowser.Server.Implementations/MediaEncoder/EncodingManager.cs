﻿using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommonIO;

namespace MediaBrowser.Server.Implementations.MediaEncoder
{
    public class EncodingManager : IEncodingManager
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly IMediaEncoder _encoder;
        private readonly IChapterManager _chapterManager;

        public EncodingManager(IFileSystem fileSystem, 
            ILogger logger, 
            IMediaEncoder encoder, 
            IChapterManager chapterManager)
        {
            _fileSystem = fileSystem;
            _logger = logger;
            _encoder = encoder;
            _chapterManager = chapterManager;
        }

        /// <summary>
        /// Gets the chapter images data path.
        /// </summary>
        /// <value>The chapter images data path.</value>
        private string GetChapterImagesPath(IHasImages item)
        {
            return Path.Combine(item.GetInternalMetadataPath(), "chapters");
        }

        /// <summary>
        /// Determines whether [is eligible for chapter image extraction] [the specified video].
        /// </summary>
        /// <param name="video">The video.</param>
        /// <returns><c>true</c> if [is eligible for chapter image extraction] [the specified video]; otherwise, <c>false</c>.</returns>
        private bool IsEligibleForChapterImageExtraction(Video video)
        {
            if (video.IsPlaceHolder)
            {
                return false;
            }

            var options = _chapterManager.GetConfiguration();

            if (video is Movie)
            {
                if (!options.EnableMovieChapterImageExtraction)
                {
                    return false;
                }
            }
            else if (video is Episode)
            {
                if (!options.EnableEpisodeChapterImageExtraction)
                {
                    return false;
                }
            }
            else
            {
                if (!options.EnableOtherVideoChapterImageExtraction)
                {
                    return false;
                }
            }

            // Can't extract images if there are no video streams
            return video.DefaultVideoStreamIndex.HasValue;
        }

        /// <summary>
        /// The first chapter ticks
        /// </summary>
        private static readonly long FirstChapterTicks = TimeSpan.FromSeconds(15).Ticks;

        public async Task<bool> RefreshChapterImages(ChapterImageRefreshOptions options, CancellationToken cancellationToken)
        {
            var extractImages = options.ExtractImages;
            var video = options.Video;
            var chapters = options.Chapters;
            var saveChapters = options.SaveChapters;

            if (!IsEligibleForChapterImageExtraction(video))
            {
                extractImages = false;
            }

            var success = true;
            var changesMade = false;

            var runtimeTicks = video.RunTimeTicks ?? 0;

            var currentImages = GetSavedChapterImages(video);

            foreach (var chapter in chapters)
            {
                if (chapter.StartPositionTicks >= runtimeTicks)
                {
                    _logger.Info("Stopping chapter extraction for {0} because a chapter was found with a position greater than the runtime.", video.Name);
                    break;
                }

                var path = GetChapterImagePath(video, chapter.StartPositionTicks);

                if (!currentImages.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    if (extractImages)
                    {
                        if (video.VideoType == VideoType.HdDvd || video.VideoType == VideoType.Iso || video.VideoType == VideoType.BluRay)
                        {
                            continue;
                        }

                        // Add some time for the first chapter to make sure we don't end up with a black image
                        var time = chapter.StartPositionTicks == 0 ? TimeSpan.FromTicks(Math.Min(FirstChapterTicks, video.RunTimeTicks ?? 0)) : TimeSpan.FromTicks(chapter.StartPositionTicks);

                        var protocol = MediaProtocol.File;

                        var inputPath = MediaEncoderHelpers.GetInputArgument(_fileSystem, video.Path, protocol, null, video.PlayableStreamFileNames);

                        try
                        {
							_fileSystem.CreateDirectory(Path.GetDirectoryName(path));

                            using (var stream = await _encoder.ExtractVideoImage(inputPath, protocol, video.Video3DFormat, time, cancellationToken).ConfigureAwait(false))
                            {
                                using (var fileStream = _fileSystem.GetFileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, true))
                                {
                                    await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                                }
                            }

                            chapter.ImagePath = path;
                            changesMade = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("Error extraching chapter images for {0}", ex, string.Join(",", inputPath));
                            success = false;
                            break;
                        }
                    }
                    else if (!string.IsNullOrEmpty(chapter.ImagePath))
                    {
                        chapter.ImagePath = null;
                        changesMade = true;
                    }
                }
                else if (!string.Equals(path, chapter.ImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    chapter.ImagePath = path;
                    changesMade = true;
                }
            }

            if (saveChapters && changesMade)
            {
                await _chapterManager.SaveChapters(video.Id.ToString(), chapters, cancellationToken).ConfigureAwait(false);
            }

            DeleteDeadImages(currentImages, chapters);

            return success;
        }

        private string GetChapterImagePath(Video video, long chapterPositionTicks)
        {
            var filename = video.DateModified.Ticks.ToString(_usCulture) + "_" + chapterPositionTicks.ToString(_usCulture) + ".jpg";

            return Path.Combine(GetChapterImagesPath(video), filename);
        }

        private List<string> GetSavedChapterImages(Video video)
        {
            var path = GetChapterImagesPath(video);

            try
            {
                return _fileSystem.GetFilePaths(path)
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                return new List<string>();
            }
        }

        private void DeleteDeadImages(IEnumerable<string> images, IEnumerable<ChapterInfo> chapters)
        {
            var deadImages = images
                .Except(chapters.Select(i => i.ImagePath).Where(i => !string.IsNullOrEmpty(i)), StringComparer.OrdinalIgnoreCase)
                .Where(i => BaseItem.SupportedImageExtensions.Contains(Path.GetExtension(i), StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var image in deadImages)
            {
                _logger.Debug("Deleting dead chapter image {0}", image);

                try
                {
                    _fileSystem.DeleteFile(image);
                }
                catch (IOException ex)
                {
                    _logger.ErrorException("Error deleting {0}.", ex, image);
                }
            }
        }
    }
}
