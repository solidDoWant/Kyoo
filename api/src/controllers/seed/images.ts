import { encode } from "blurhash";
import { type SQL, and, eq, is, lt, sql } from "drizzle-orm";
import { PgColumn, type PgTable } from "drizzle-orm/pg-core";
import path from "node:path";
import { version } from "package.json";
import type { PoolClient } from "pg";
import sharp from "sharp";
import { type Transaction, db } from "~/db";
import { mqueue } from "~/db/schema/mqueue";
import type { Image } from "~/models/utils";
import { getFile } from "~/utils";

export const imageDir = process.env.IMAGES_PATH ?? "./images";
export const defaultBlurhash = "000000";

type ImageTask = {
	id: string;
	url: string;
	table: string;
	column: string;
};

// this will only push a task to the image downloader service and not download it instantly.
// this is both done to prevent to many requests to be sent at once and to make sure POST
// requests are not blocked by image downloading or blurhash calculation
export const enqueueOptImage = async (
	tx: Transaction,
	img:
		| { url: string | null; column: PgColumn }
		| { url: string | null; table: PgTable; column: SQL },
): Promise<Image | null> => {
	if (!img.url) return null;

	const hasher = new Bun.CryptoHasher("sha256");
	hasher.update(img.url);
	const id = hasher.digest().toString("hex");

	const cleanupColumn = (column: SQL) =>
		// @ts-expect-error dialect is private
		db.dialect.sqlToQuery(
			sql.join(
				column.queryChunks.map((x) => {
					if (is(x, PgColumn)) {
						return sql.identifier(x.name);
					}
					return x;
				}),
			),
		).sql;

	const message: ImageTask =
		"table" in img
			? {
					id,
					url: img.url,
					// @ts-expect-error dialect is private
					table: db.dialect.sqlToQuery(sql`${img.table}`).sql,
					column: cleanupColumn(img.column),
				}
			: {
					id,
					url: img.url,
					// @ts-expect-error dialect is private
					table: db.dialect.sqlToQuery(sql`${img.column.table}`).sql,
					column: sql.identifier(img.column.name).value,
				};
	await tx.insert(mqueue).values({
		kind: "image",
		message,
	});
	await tx.execute(sql`notify image`);

	return {
		id,
		source: img.url,
		blurhash: defaultBlurhash,
	};
};

export const processImages = async () => {
	async function processOne() {
		return await db.transaction(async (tx) => {
			const [item] = await tx
				.select()
				.from(mqueue)
				.for("update", { skipLocked: true })
				.where(and(eq(mqueue.kind, "image"), lt(mqueue.attempt, 5)))
				.orderBy(mqueue.attempt, mqueue.createdAt)
				.limit(1);

			if (!item) return false;

			const img = item.message as ImageTask;
			try {
				const blurhash = await downloadImage(img.id, img.url);
				const ret: Image = { id: img.id, source: img.url, blurhash };

				const table = sql.raw(img.table);
				const column = sql.raw(img.column);

				await tx.execute(sql`
				update ${table} set ${column} = ${ret} where ${column}->'id' = ${sql.raw(`'"${img.id}"'::jsonb`)}
			`);

				await tx.delete(mqueue).where(eq(mqueue.id, item.id));
			} catch (err) {
				console.error("Failed to download image", img.url, err);
				await tx
					.update(mqueue)
					.set({ attempt: sql`${mqueue.attempt}+1` })
					.where(eq(mqueue.id, item.id));
			}
			return true;
		});
	}

	let running = false;
	async function processAll() {
		if (running) return;
		running = true;

		let found = true;
		while (found) {
			found = await processOne();
		}
		running = false;
	}

	const client = (await db.$client.connect()) as PoolClient;
	client.on("notification", (evt) => {
		if (evt.channel !== "image") return;
		processAll();
	});
	await client.query("listen image");

	// start processing old tasks
	await processAll();
	return () => client.release(true);
};

async function downloadImage(id: string, url: string): Promise<string> {
	// TODO: check if file exists before downloading
	const resp = await fetch(url, {
		headers: { "User-Agent": `Kyoo v${version}` },
	});
	if (!resp.ok) {
		throw new Error(`Failed to fetch image: ${resp.status} ${resp.statusText}`);
	}
	const buf = Buffer.from(await resp.arrayBuffer());

	const image = sharp(buf);
	const metadata = await image.metadata();

	if (!metadata.width || !metadata.height) {
		throw new Error("Could not determine image dimensions");
	}
	const resolutions = {
		low: { width: 320 },
		medium: { width: 640 },
		high: { width: 1280 },
	};
	await Promise.all(
		Object.entries(resolutions).map(async ([resolution, dimensions]) => {
			const buffer = await image.clone().resize(dimensions.width).toBuffer();
			const file = getFile(path.join(imageDir, `${id}.${resolution}.jpg`));

			await Bun.write(file, buffer, { mode: 0o660 });
		}),
	);

	const { data, info } = await image
		.resize(32, 32, { fit: "inside" })
		.ensureAlpha()
		.raw()
		.toBuffer({ resolveWithObject: true });

	const blurHash = encode(
		new Uint8ClampedArray(data),
		info.width,
		info.height,
		4,
		3,
	);

	return blurHash;
}
