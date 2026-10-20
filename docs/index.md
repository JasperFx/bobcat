---
layout: home
title: Bobcat
titleTemplate: Integration testing for .NET

hero:
  name: ""
  text: ""
  tagline: Author, specify, and supervise integration tests in .NET.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/JasperFx/bobcat

features:
  - title: Author
    details: Behavior Driven Development with ordinary xUnit, TUnit, NUnit or MSTest tests against any .NET application. Keep your existing testing tools, and let Bobcat render the tests as human readable specifications through decorated step helpers and marker comments.
    link: /marker-steps
    linkText: Write your first specification
  - title: Specify
    details: Spec-driven development end to end -- executable specifications that still read as requirements, with tables, decision tables, set verification and partial objects for the data-intensive cases.
    link: /tutorials/specifications-with-code
    linkText: Specifications with code
  - title: Supervise
    details: Make a big integration suite worth trusting -- split across worker processes, resources isolated per lane, retry budgets, and flakiness reported honestly instead of buried.
    link: /tutorials/reliable-integration-testing
    linkText: Make existing tests more reliable
  - title: Event Modeling and the Critter Stack
    details: Optional, and the best way in for a Wolverine, Marten, Polecat or Fisher application. Import an Event Model as C#, write specifications in a ready-made Given/When/Then vocabulary, bind each one to its slice, and see which slices the specifications prove.
    link: /tutorials/event-modeling
    linkText: Build from an Event Model
  - title: Observe
    details: The free <code>bobcat ui</code> serves a live run board and the Event Model canvas, colouring every slice from the runs of the specifications bound to it, with results files and a JSON report for everything else.
    link: /bobcat-ui
    linkText: Watch a run
  - title: Built for AI agents
    details: An optimized feedback loop for AI agent development. Customize the telemetry to describe what the system actually did during a failed test run instead of just saying that it failed, CLI queries over runs and specifications, and warm re-runs in seconds -- so a coding agent can diagnose a red suite and fix it without a human reading the output for it.
    link: /agent-queries
    linkText: Query runs from the CLI
---
