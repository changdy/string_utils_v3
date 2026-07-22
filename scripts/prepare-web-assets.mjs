#!/usr/bin/env node

/**
 * Prepare the latest JSON Hero and JSONCrack static assets for StrToolkit.
 *
 * JSON Hero:
 *   Downloads the pre-built .tar.gz asset from the latest
 *   changdy/json-hero-frontend GitHub Release.
 *
 * JSONCrack:
 *   Downloads the source tarball from the latest
 *   AykutSarac/jsoncrack.com GitHub Release, installs/builds in a temporary
 *   directory, and copies only apps/www/out into the final asset directory.
 *
 * No source checkout, node_modules directory, npm/pnpm cache, or package
 * manager metadata is copied into StrToolkit's build or publish output.
 *
 * Usage:
 *   node scripts/prepare-web-assets.mjs [--output <directory>] [--force]
 *
 * Environment:
 *   GITHUB_TOKEN / GH_TOKEN  Optional GitHub API token.
 */

import { spawnSync } from "node:child_process";
import { createWriteStream } from "node:fs";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(SCRIPT_DIR, "..");
const DEFAULT_OUTPUT = path.join(PROJECT_ROOT, ".web-assets");

const JSON_HERO_REPOSITORY = "changdy/json-hero-frontend";
const JSON_CRACK_REPOSITORY = "AykutSarac/jsoncrack.com";
const USER_AGENT = "StrToolkit-web-assets-builder";
const TOKEN = process.env.GITHUB_TOKEN || process.env.GH_TOKEN;

const args = process.argv.slice(2);
const force = args.includes("--force");
const outputArgumentIndex = args.indexOf("--output");
if (outputArgumentIndex >= 0 && !args[outputArgumentIndex + 1]) {
  fail("--output requires a directory argument");
}
const outputDirectory = path.resolve(
  outputArgumentIndex >= 0 ? args[outputArgumentIndex + 1] : DEFAULT_OUTPUT
);
if (path.parse(outputDirectory).root === outputDirectory) {
  fail(`Refusing to use a filesystem root as the output directory: ${outputDirectory}`);
}

function log(message) {
  console.log(`[web-assets] ${message}`);
}

function fail(message) {
  console.error(`[web-assets] ${message}`);
  process.exit(1);
}

function githubHeaders() {
  return {
    Accept: "application/vnd.github+json",
    "User-Agent": USER_AGENT,
    ...(TOKEN ? { Authorization: `Bearer ${TOKEN}` } : {}),
  };
}

async function githubJson(url) {
  const response = await fetch(url, { headers: githubHeaders() });
  if (!response.ok) {
    throw new Error(`GitHub API ${response.status} ${response.statusText}: ${url}`);
  }
  return response.json();
}

async function download(url, destination) {
  const response = await fetch(url, { headers: githubHeaders(), redirect: "follow" });
  if (!response.ok || !response.body) {
    throw new Error(`Download failed (${response.status} ${response.statusText}): ${url}`);
  }
  await pipeline(Readable.fromWeb(response.body), createWriteStream(destination));
}

function run(command, commandArgs, options = {}) {
  log(`> ${command} ${commandArgs.join(" ")}`);
  const result = spawnSync(command, commandArgs, {
    cwd: options.cwd,
    env: { ...process.env, ...options.env },
    encoding: "utf8",
    stdio: options.inherit ? "inherit" : "pipe",
    windowsHide: true,
    timeout: options.timeout,
  });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    const details = [result.stdout, result.stderr].filter(Boolean).join("\n").trim();
    throw new Error(
      `${command} exited with code ${result.status}${details ? `\n${details}` : ""}`
    );
  }
}

function runPnpm(pnpmArgs, options = {}) {
  if (process.platform === "win32") {
    // Node 24 rejects spawning .cmd files directly with EINVAL. All arguments
    // passed here are fixed by this script, so invoking Corepack through the
    // system command processor is both predictable and safe.
    return run(process.env.ComSpec || "cmd.exe", [
      "/d",
      "/s",
      "/c",
      `corepack pnpm ${pnpmArgs.join(" ")}`,
    ], options);
  }
  return run("corepack", ["pnpm", ...pnpmArgs], options);
}

async function createPnpmShim(directory) {
  await fs.mkdir(directory, { recursive: true });
  if (process.platform === "win32") {
    await fs.writeFile(
      path.join(directory, "pnpm.cmd"),
      "@echo off\r\ncorepack pnpm %*\r\n"
    );
    return;
  }

  const shim = path.join(directory, "pnpm");
  await fs.writeFile(shim, "#!/usr/bin/env sh\nexec corepack pnpm \"$@\"\n");
  await fs.chmod(shim, 0o755);
}

async function extractTarGz(archive, destination) {
  await fs.mkdir(destination, { recursive: true });
  run("tar", ["-xzf", archive, "-C", destination]);
}

async function findDirectoryContaining(root, requiredRelativePath, maxDepth = 3) {
  const queue = [{ directory: root, depth: 0 }];
  while (queue.length > 0) {
    const current = queue.shift();
    if (await exists(path.join(current.directory, requiredRelativePath))) {
      return current.directory;
    }
    if (current.depth >= maxDepth) continue;

    for (const entry of await fs.readdir(current.directory, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        queue.push({
          directory: path.join(current.directory, entry.name),
          depth: current.depth + 1,
        });
      }
    }
  }
  return null;
}

async function exists(target) {
  try {
    await fs.access(target);
    return true;
  } catch {
    return false;
  }
}

async function removeSourceMaps(directory) {
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await removeSourceMaps(target);
    } else if (entry.name.endsWith(".map")) {
      await fs.rm(target, { force: true });
    }
  }
}

async function assertStaticOnly(directory, requiredEntry) {
  if (!(await exists(path.join(directory, requiredEntry)))) {
    throw new Error(`Required static entry is missing: ${path.join(directory, requiredEntry)}`);
  }

  const forbiddenDirectories = new Set(["node_modules", ".git", ".pnpm-store"]);
  const forbiddenFiles = new Set([
    "package.json",
    "package-lock.json",
    "pnpm-lock.yaml",
    "yarn.lock",
  ]);
  const queue = [directory];

  while (queue.length > 0) {
    const current = queue.pop();
    for (const entry of await fs.readdir(current, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (forbiddenDirectories.has(entry.name)) {
          throw new Error(`Non-static directory reached final assets: ${path.join(current, entry.name)}`);
        }
        queue.push(path.join(current, entry.name));
      } else if (forbiddenFiles.has(entry.name)) {
        throw new Error(`Package manager file reached final assets: ${path.join(current, entry.name)}`);
      }
    }
  }
}

async function copyReplacing(source, destination) {
  const incoming = `${destination}.incoming`;
  await fs.rm(incoming, { recursive: true, force: true });
  await fs.cp(source, incoming, { recursive: true, force: true });
  await fs.rm(destination, { recursive: true, force: true });
  await fs.rename(incoming, destination);
}

async function readCurrentVersions() {
  try {
    return JSON.parse(await fs.readFile(path.join(outputDirectory, "versions.json"), "utf8"));
  } catch {
    return null;
  }
}

async function main() {
  log("Resolving latest GitHub Releases...");
  const [heroRelease, crackRelease] = await Promise.all([
    githubJson(`https://api.github.com/repos/${JSON_HERO_REPOSITORY}/releases/latest`),
    githubJson(`https://api.github.com/repos/${JSON_CRACK_REPOSITORY}/releases/latest`),
  ]);

  const heroAsset = heroRelease.assets.find(
    asset => asset.name.startsWith("json-hero-frontend-") && asset.name.endsWith(".tar.gz")
  );
  if (!heroAsset) {
    throw new Error(
      `Latest ${JSON_HERO_REPOSITORY} release ${heroRelease.tag_name} has no pre-built .tar.gz asset`
    );
  }

  const current = await readCurrentVersions();
  const cached =
    !force &&
    current?.jsonHero?.tag === heroRelease.tag_name &&
    current?.jsonCrack?.tag === crackRelease.tag_name &&
    (await exists(path.join(outputDirectory, "jsonhero-frontend", "index.html"))) &&
    (await exists(path.join(outputDirectory, "json-crack", "editor.html")));

  if (cached) {
    log(
      `Assets are current: JSON Hero ${heroRelease.tag_name}, JSONCrack ${crackRelease.tag_name}`
    );
    return;
  }

  const nodeMajor = Number(process.versions.node.split(".")[0]);
  if (nodeMajor < 24) {
    throw new Error(
      `JSONCrack ${crackRelease.tag_name} requires Node.js 24 or newer; current version is ${process.version}`
    );
  }

  const temporaryRoot = await fs.mkdtemp(path.join(os.tmpdir(), "str-toolkit-web-assets-"));
  try {
    const commandShimDirectory = path.join(temporaryRoot, "command-shims");
    await createPnpmShim(commandShimDirectory);
    const packageManagerEnvironment = {
      CI: "true",
      NEXT_TELEMETRY_DISABLED: "1",
      TURBO_TELEMETRY_DISABLED: "1",
      PATH: `${commandShimDirectory}${path.delimiter}${process.env.PATH || ""}`,
    };

    const preparedRoot = path.join(temporaryRoot, "prepared");
    const preparedHero = path.join(preparedRoot, "jsonhero-frontend");
    const preparedCrack = path.join(preparedRoot, "json-crack");
    await fs.mkdir(preparedRoot, { recursive: true });

    log(`Downloading JSON Hero ${heroRelease.tag_name}: ${heroAsset.name}`);
    const heroArchive = path.join(temporaryRoot, "jsonhero.tar.gz");
    const heroExtract = path.join(temporaryRoot, "jsonhero-extract");
    await download(heroAsset.browser_download_url, heroArchive);
    await extractTarGz(heroArchive, heroExtract);
    const heroStatic = await findDirectoryContaining(heroExtract, "index.html");
    if (!heroStatic) throw new Error("JSON Hero release archive does not contain index.html");
    await fs.cp(heroStatic, preparedHero, { recursive: true });
    await assertStaticOnly(preparedHero, "index.html");

    log(`Downloading JSONCrack source release ${crackRelease.tag_name}`);
    const crackArchive = path.join(temporaryRoot, "jsoncrack-source.tar.gz");
    const crackExtract = path.join(temporaryRoot, "jsoncrack-source");
    await download(crackRelease.tarball_url, crackArchive);
    await extractTarGz(crackArchive, crackExtract);
    const crackSource = await findDirectoryContaining(crackExtract, "apps/www/package.json");
    if (!crackSource) throw new Error("JSONCrack source archive has an unexpected layout");

    const nextConfig = path.join(crackSource, "apps", "www", "next.config.js");
    if (await exists(nextConfig)) {
      const config = await fs.readFile(nextConfig, "utf8");
      if (config.includes("productionBrowserSourceMaps: true")) {
        await fs.writeFile(
          nextConfig,
          config.replace("productionBrowserSourceMaps: true", "productionBrowserSourceMaps: false")
        );
        log("Disabled JSONCrack production browser source maps");
      }
    }

    log("Installing JSONCrack dependencies in the temporary source directory...");
    runPnpm(["install", "--frozen-lockfile"], {
      cwd: crackSource,
      inherit: true,
      timeout: 10 * 60 * 1000,
      env: packageManagerEnvironment,
    });

    log("Building the JSONCrack static www export...");
    runPnpm(["build:www"], {
      cwd: crackSource,
      inherit: true,
      timeout: 15 * 60 * 1000,
      env: packageManagerEnvironment,
    });

    const crackStatic = path.join(crackSource, "apps", "www", "out");
    if (!(await exists(crackStatic))) {
      throw new Error(`JSONCrack static output was not created: ${crackStatic}`);
    }
    await fs.cp(crackStatic, preparedCrack, { recursive: true });
    await removeSourceMaps(preparedCrack);
    await assertStaticOnly(preparedCrack, "editor.html");

    await fs.mkdir(outputDirectory, { recursive: true });
    await copyReplacing(preparedHero, path.join(outputDirectory, "jsonhero-frontend"));
    await copyReplacing(preparedCrack, path.join(outputDirectory, "json-crack"));

    const versions = {
      preparedAt: new Date().toISOString(),
      jsonHero: {
        repository: JSON_HERO_REPOSITORY,
        tag: heroRelease.tag_name,
        asset: heroAsset.name,
        release: heroRelease.html_url,
      },
      jsonCrack: {
        repository: JSON_CRACK_REPOSITORY,
        tag: crackRelease.tag_name,
        source: crackRelease.tarball_url,
        release: crackRelease.html_url,
      },
    };
    await fs.writeFile(
      path.join(outputDirectory, "versions.json"),
      `${JSON.stringify(versions, null, 2)}\n`
    );

    log(`Prepared JSON Hero ${heroRelease.tag_name}`);
    log(`Prepared JSONCrack ${crackRelease.tag_name}`);
    log(`Static-only output: ${outputDirectory}`);
  } finally {
    log("Removing temporary source, node_modules, and package-manager files...");
    await fs.rm(temporaryRoot, { recursive: true, force: true });
  }
}

main().catch(error => {
  console.error(`[web-assets] Fatal: ${error.stack || error.message}`);
  process.exitCode = 1;
});
