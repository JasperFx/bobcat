---
layout: home
title: Bobcat
titleTemplate: Integration testing for .NET

hero:
  name: ""
  text: ""
  tagline: Author, supervise, and run integration tests in .NET.
  actions:
    - theme: brand
      text: Get started
      link: /getting-started
    - theme: alt
      text: View on GitHub
      link: https://github.com/JasperFx/bobcat

features:
  - title: Author
    details: Specifications as Gherkin `.feature` files, bound to plain C# fixture methods with Cucumber expressions. Or author them in plain C# instead — an xUnit v3 or TUnit suite carries the specifications itself, through marker comments and `[BobcatStep]` helpers.
    link: /sample-wiring
    linkText: Wire a sample host
  - title: Supervise
    details: Make a big integration suite worth trusting — split across worker processes, resources isolated per lane, retry budgets, and flakiness reported honestly instead of buried.
    link: /parallel-ready-suites
    linkText: Make existing tests more reliable
  - title: Specify
    details: Spec-driven development end to end — executable specifications that still read as requirements, and slice-by-slice scaffolding straight from an Event Model.
    link: /command-line#import-event-model
    linkText: Import an Event Model
---
