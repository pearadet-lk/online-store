import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App.vue'

describe('Vue feature parity', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input) => {
        const url = String(input)
        if (url.includes('/api/products')) {
          return new Response(
            JSON.stringify([
              {
                productId: '11111111-1111-1111-1111-111111111111',
                name: 'Starter Keyboard',
                description: 'Entry-level keyboard',
                price: 39.99,
              },
            ]),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          )
        }

        return new Response('{}', {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders shared feature sections and checkout action', async () => {
    const wrapper = mount(App)
    await flushPromises()

    expect(wrapper.find('h1').text()).toContain('Online Store')
    expect(wrapper.findAll('h2').map((h) => h.text())).toEqual(
      expect.arrayContaining(['Login', 'Register', 'Catalog', 'Cart']),
    )
    expect(
      wrapper.findAll('button').some((b) => b.text().includes('Checkout')),
    ).toBe(true)
  })
})
