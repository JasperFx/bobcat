// Generates the SPA's TypeScript wire contracts (bobcat#604) before `npm ci`, `dev`, `test`
// and `build`: src/messages/monitor-events.ts and src/messages/relayRoutes.ts, both gitignored.
//
// They come from the C# records in src/Bobcat.Ui/Contracts, through the bobcat tool's
// `generate-ui-contracts` command. The newest BUILT tool is used when there is one, so after one
// `dotnet build` this costs a second rather than a build; with none, `dotnet run` builds it.
//
// BOBCAT_UI_CONTRACTS_SKIP=1 skips it: the tool's own pack (EmbedFrontend, inside a dotnet build)
// and ui-frontend.yml set it, because there `./build.sh UiContracts` has already generated them
// and a nested `dotnet run` from inside a build would be a build inside a build.
import { spawnSync } from 'node:child_process'
import { existsSync, statSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const frontEnd = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const source = join(frontEnd, 'src')
const repository = resolve(frontEnd, '..', '..')
const consoleProject = join(repository, 'src', 'Bobcat.Console')
const generated = ['monitor-events.ts', 'relayRoutes.ts'].map((f) => join(source, 'messages', f))
const present = () => generated.every((f) => existsSync(f))

if (process.env.BOBCAT_UI_CONTRACTS_SKIP === '1') {
  if (!present()) {
    console.error('BOBCAT_UI_CONTRACTS_SKIP=1, but the generated contracts are missing. Run `./build.sh UiContracts`.')
    process.exit(1)
  }
  process.exit(0)
}

const builtTools = ['Debug', 'Release']
  .map((configuration) => join(consoleProject, 'bin', configuration, 'net10.0', 'Bobcat.Cli.dll'))
  .filter((dll) => existsSync(dll))
  .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)

const args = builtTools.length > 0
  ? [builtTools[0], 'generate-ui-contracts', '--front-end', source]
  : ['run', '--project', consoleProject, '--', 'generate-ui-contracts', '--front-end', source]

const result = spawnSync('dotnet', args, { stdio: 'inherit', cwd: repository, env: { ...process.env, BOBCAT_MONITOR: '0' } })

if (result.status !== 0) {
  const why = result.error ? `dotnet could not be started (${result.error.message})` : `dotnet exited ${result.status}`
  if (present()) {
    console.warn(`Could not regenerate the TypeScript contracts (${why}); using the ones already generated.`)
    process.exit(0)
  }
  console.error(`Could not generate src/messages/monitor-events.ts and relayRoutes.ts (${why}).`)
  console.error('They are built from the C# contracts: run `dotnet build` (or `./build.sh UiContracts`) at the repository root.')
  process.exit(1)
}
