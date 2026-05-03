import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render app heading', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Online Store (Angular - Gateway)');
  });

  it('should render shared feature sections', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const sectionTitles = Array.from(compiled.querySelectorAll('h2')).map((x) => x.textContent?.trim());

    expect(sectionTitles).toContain('Login');
    expect(sectionTitles).toContain('Register');
    expect(sectionTitles).toContain('Catalog');
    expect(sectionTitles).toContain('Cart');
  });

  it('should render checkout action', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    const buttons = Array.from(compiled.querySelectorAll('button')).map((x) => x.textContent?.trim() ?? '');

    expect(buttons.some((x) => x.includes('Checkout'))).toBeTrue();
  });
});
