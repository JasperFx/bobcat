// See https://aka.ms/new-console-template for more information

using Bobcat.Rendering;

var line = new Line
{
    { "The sum of ", Mode.Text },
    { "25", Mode.Input },
    { " and ", Mode.Text },
    { "50", Mode.Input },
    { " should be ", Mode.Text },
    { "74, but was 75", Mode.Wrong }
};

var renderer = new CommandLineRenderer();

renderer.Render(line);