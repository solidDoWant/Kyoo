// Kyoo - A portable and vast media library solution.
// Copyright (c) Kyoo.
//
// See AUTHORS.md and LICENSE file in the project root for full license information.
//
// Kyoo is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// Kyoo is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Kyoo. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Blurhash.SkiaSharp;
using Kyoo.Abstractions.Controllers;
using Kyoo.Abstractions.Models;
using Kyoo.Abstractions.Models.Exceptions;
using Kyoo.Core.Storage;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SKSvg = SkiaSharp.Extended.Svg.SKSvg;

namespace Kyoo.Core.Controllers;

/// <summary>
/// Download images and retrieve the path of those images for a resource.
/// </summary>
public class ThumbnailsManager(
	IHttpClientFactory clientFactory,
	ILogger<ThumbnailsManager> logger,
	IStorage storage,
	Lazy<IRepository<User>> users
) : IThumbnailsManager
{
	private async Task _SaveImage(SKBitmap bitmap, string path, int quality)
	{
		SKData data = bitmap.Encode(SKEncodedImageFormat.Webp, quality);
		await using Stream reader = data.AsStream();
		await storage.Write(reader, path);
	}

	private SKBitmap _SKBitmapFrom(Stream reader, bool isSvg)
	{
		if (isSvg)
		{
			SKSvg svg = new();
			svg.Load(reader);
			SKBitmap bitmap = new((int)svg.CanvasSize.Width, (int)svg.CanvasSize.Height);
			using SKCanvas canvas = new(bitmap);
			canvas.DrawPicture(svg.Picture);
			return bitmap;
		}

		using SKCodec codec = SKCodec.Create(reader);
		if (codec == null)
			throw new NotSupportedException("Unsupported codec");

		SKImageInfo info = codec.Info;
		info.ColorType = SKColorType.Rgba8888;
		return SKBitmap.Decode(codec, info);
	}

	public async Task DownloadImage(Image? image, string what)
	{
		if (image == null)
			return;
		try
		{
			if (image.Id == Guid.Empty)
			{
				// Ensure stable ids to prevent duplicated images being stored on the fs.
				using MD5 md5 = MD5.Create();
				image.Id = new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(image.Source)));
			}

			logger.LogInformation("Downloading image {What}", what);

			HttpClient client = clientFactory.CreateClient();
			HttpResponseMessage response = await client.GetAsync(image.Source);
			response.EnsureSuccessStatusCode();
			await using Stream reader = await response.Content.ReadAsStreamAsync();
			using SKBitmap original = _SKBitmapFrom(
				reader,
				isSvg: response.Content.Headers.ContentType?.MediaType?.Contains("svg") == true
			);

			using SKBitmap high = original.Resize(
				new SKSizeI(original.Width, original.Height),
				SKFilterQuality.High
			);
			await _SaveImage(original, _GetImagePath(image.Id, ImageQuality.High), 90);

			using SKBitmap medium = high.Resize(
				new SKSizeI((int)(high.Width / 1.5), (int)(high.Height / 1.5)),
				SKFilterQuality.Medium
			);
			await _SaveImage(medium, _GetImagePath(image.Id, ImageQuality.Medium), 75);

			using SKBitmap low = medium.Resize(
				new SKSizeI(original.Width / 2, original.Height / 2),
				SKFilterQuality.Low
			);
			await _SaveImage(low, _GetImagePath(image.Id, ImageQuality.Low), 50);

			image.Blurhash = Blurhasher.Encode(low, 4, 3);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "{What} could not be downloaded", what);
		}
	}

	/// <inheritdoc />
	public async Task DownloadImages<T>(T item)
		where T : IThumbnails
	{
		string name = item is IResource res ? res.Slug : "???";

		await DownloadImage(item.Poster, $"The poster of {name}");
		await DownloadImage(item.Thumbnail, $"The thumbnail of {name}");
		await DownloadImage(item.Logo, $"The logo of {name}");
	}

	public async Task<bool> IsImageSaved(Guid imageId, ImageQuality quality) =>
		await storage.DoesExist(_GetImagePath(imageId, quality));

	public async Task<Stream> GetImage(Guid imageId, ImageQuality quality)
	{
		string path = _GetImagePath(imageId, quality);
		if (await storage.DoesExist(path))
			return await storage.Read(path);

		throw new ItemNotFoundException();
	}

	/// <inheritdoc />
	private string _GetImagePath(Guid imageId, ImageQuality quality)
	{
		return $"/metadata/{imageId}.{quality.ToString().ToLowerInvariant()}.webp";
	}

	/// <inheritdoc />
	public Task DeleteImages<T>(T item)
		where T : IThumbnails
	{
		var imageDeletionTasks = new[] { item.Poster?.Id, item.Thumbnail?.Id, item.Logo?.Id }
			.Where(x => x is not null)
			.SelectMany(x => $"/metadata/{x}")
			.SelectMany(x =>
				new[]
				{
					ImageQuality.High.ToString().ToLowerInvariant(),
					ImageQuality.Medium.ToString().ToLowerInvariant(),
					ImageQuality.Low.ToString().ToLowerInvariant(),
				}.Select(quality => $"{x}.{quality}.webp")
			)
			.Select(storage.Delete);

		return Task.WhenAll(imageDeletionTasks);
	}

	public async Task<Stream> GetUserImage(Guid userId)
	{
		var filePath = $"/metadata/user/{userId}.webp";
		if (await storage.DoesExist(filePath))
			return await storage.Read(filePath);

		User user = await users.Value.Get(userId);
		if (user.Email == null)
			throw new ItemNotFoundException();
		using MD5 md5 = MD5.Create();
		string hash = Convert
			.ToHexString(md5.ComputeHash(Encoding.ASCII.GetBytes(user.Email)))
			.ToLower();
		try
		{
			HttpClient client = clientFactory.CreateClient();
			HttpResponseMessage response = await client.GetAsync(
				$"https://www.gravatar.com/avatar/{hash}.jpg?d=404&s=250"
			);
			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsStreamAsync();
		}
		catch
		{
			throw new ItemNotFoundException();
		}
	}

	public async Task SetUserImage(Guid userId, Stream? image)
	{
		var filePath = $"/metadata/user/{userId}.webp";
		if (image == null && await storage.DoesExist(filePath))
		{
			await storage.Delete(filePath);
			return;
		}

		using SKCodec codec = SKCodec.Create(image);
		SKImageInfo info = codec.Info;
		info.ColorType = SKColorType.Rgba8888;
		using SKBitmap original = SKBitmap.Decode(codec, info);
		using SKBitmap ret = original.Resize(new SKSizeI(250, 250), SKFilterQuality.High);
		await _SaveImage(ret, filePath, 75);
	}
}
