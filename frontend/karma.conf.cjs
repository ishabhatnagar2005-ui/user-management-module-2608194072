// Run Angular/Jasmine components through native headless Chrome.
module.exports = function (config) {
  config.set({ frameworks: ['jasmine'], plugins: [require('karma-jasmine'), require('karma-chrome-launcher')],
    reporters: ['progress'], browsers: ['ChromeHeadlessNoSandbox'],
    customLaunchers: { ChromeHeadlessNoSandbox: { base: 'ChromeHeadless', flags: ['--no-sandbox', '--disable-dev-shm-usage'] } },
    singleRun: true, restartOnFileChange: false, client: { jasmine: { random: false } } });
};
