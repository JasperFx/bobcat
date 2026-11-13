import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { BobcatResults } from '@/messages/results-file'
import BobcatSpec from '../BobcatSpec.vue'
import { BobcatSpecElement, ELEMENT_NAME, hostTheme } from '../main'
import fixture from '../../results/__tests__/results.fixture.json'

/**
 * The `<bobcat-spec>` element (bobcat#553): a documentation page's one element, fetching the
 * results `bobcat results-docs` wrote and rendering one specification through the board's own
 * viewer. The fetch is swapped for the fixture through the `bobcatSpecFetch` seam the element reads.
 */
const results = fixture as BobcatResults
const g = globalThis as { bobcatSpecFetch?: (src: string) => Promise<unknown> }

beforeEach(() => {
  g.bobcatSpecFetch = async (src) => {
    if (src === 'results.json') return { files: [{ attachmentBase: 'results/', results }] }
    if (src === 'run.json') return results
    throw new Error('404 Not Found')
  }
})

afterEach(() => {
  delete g.bobcatSpecFetch
})

async function spec(src: string, id: string) {
  const wrapper = mount(BobcatSpec, { props: { src, spec: id } })
  await flushPromises()
  return wrapper
}

describe('the bobcat-spec component', () => {
  it('renders the named specification through the specification view', async () => {
    const wrapper = await spec('results.json', 'Sets/a set of names missing one and expecting one that is not there')

    const scenario = wrapper.get('[data-testid="spec-scenario"]')
    expect(scenario.text()).toContain('a set of names missing one and expecting one that is not there')
    expect(scenario.text()).toContain('failed')
    expect(scenario.find('[data-testid="specification"]').exists()).toBe(true)
    expect(scenario.findAll('[data-testid="row-marker"]').map((m) => m.text())).toEqual(['', '', 'missing', 'extra'])
    expect(wrapper.get('[data-testid="bobcat-spec"]').attributes('data-status')).toBe('failed')
  })

  it('reads a run\'s own results file as well as the folded results.json', async () => {
    const wrapper = await spec('run.json', 'Actions/custom logging from a step')

    expect(wrapper.get('[data-testid="spec-scenario"]').text()).toContain('passed')
    expect(wrapper.get('[data-testid="report"]').text()).toContain('Envelopes')
  })

  it('links attachments through the results base the folded file carries', async () => {
    const wrapper = await spec('results.json', 'Calculator/an exception in an action stops the scenario')

    expect(wrapper.get('img').attributes('src')).toBe('results/attachments/run/spec/01-screen.png')
  })

  it('says when the identity is not in the results', async () => {
    const wrapper = await spec('results.json', 'Nowhere/nothing')

    expect(wrapper.get('[data-testid="spec-missing"]').text()).toBe('No specification named “Nowhere/nothing” in results.json.')
    expect(wrapper.get('[data-testid="bobcat-spec"]').attributes('data-status')).toBe('none')
  })

  it('says when the results cannot be fetched', async () => {
    const wrapper = await spec('missing.json', 'Sets/anything')

    expect(wrapper.get('[data-testid="spec-error"]').text()).toBe('Could not read missing.json: 404 Not Found')
  })

  it('says what it needs when an attribute is missing', async () => {
    expect((await spec('', 'Sets/anything')).get('[data-testid="spec-error"]').text()).toContain('bobcat-spec needs a src')
    expect((await spec('results.json', '')).get('[data-testid="spec-error"]').text()).toContain('bobcat-spec needs a spec')
  })
})

// The Vitest project has no DOM lib (see tsconfig.vitest.json), so the document is reached by shape.
interface Node {
  textContent: string | null
}
interface Host {
  setAttribute(name: string, value: string): void
  remove(): void
  shadowRoot: { querySelector(selector: string): Node | null; querySelectorAll(selector: string): Iterable<Node> } | null
}
const dom = globalThis as unknown as {
  customElements: { get(name: string): unknown }
  document: { createElement(tag: string): Host; body: { appendChild(host: Host): void } }
}

describe('the bobcat-spec custom element', () => {
  it('is defined once, and renders into its shadow root from its attributes', async () => {
    expect(dom.customElements.get(ELEMENT_NAME)).toBe(BobcatSpecElement)

    const element = dom.document.createElement(ELEMENT_NAME)
    element.setAttribute('src', 'results.json')
    element.setAttribute('spec', 'Actions/custom logging from a step')
    dom.document.body.appendChild(element)
    await flushPromises()
    await flushPromises()

    const root = element.shadowRoot
    expect(root).not.toBeNull()
    expect(root!.querySelector('[data-testid="spec-scenario"]')?.textContent).toContain('custom logging from a step')
    // The theme rides inside the shadow root as a style element. (Vitest hands every CSS import
    // over empty, so its text is checked through hostTheme below rather than here.)
    expect([...root!.querySelectorAll('style')].length).toBeGreaterThan(0)
    element.remove()
  })

  it('re-addresses the board\'s colour tokens from the page to the shadow host', () => {
    const theme = hostTheme(':root {\n  --bc-accent: #f0a23b;\n}\n\nhtml.dark {\n  --el-bg-color: var(--bc-surface);\n}\nbody { margin: 0; }\n')

    expect(theme).toBe(':host {\n  --bc-accent: #f0a23b;\n}\n\n:host {\n  --el-bg-color: var(--bc-surface);\n}\nbody { margin: 0; }\n')
  })
})
