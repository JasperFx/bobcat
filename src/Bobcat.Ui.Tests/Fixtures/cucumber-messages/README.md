# Cucumber Messages schema (bobcat#848)

`messages.schema.json` is the bundled JSON Schema (draft 2020-12) of the Cucumber Messages protocol,
copied byte for byte from https://github.com/cucumber/messages, `jsonschema/messages.schema.json`,
commit `edb67babab4404cc8f7d40535086b1d088246d72` (2026-11-27, release v35.0.0). Licence: MIT
(`LICENSE`, copied from the same repo).

`CucumberMessagesExportTests` validates every envelope `bobcat export-run --format cucumber-messages`
writes against it. The file is test data: nothing executes it, and nothing in it is compiled.
