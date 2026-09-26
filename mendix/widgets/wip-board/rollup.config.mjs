// Studio Pro cung cấp mx-api; không đóng gói bản client riêng vào widget.
export default args => args.configDefaultConfig.map(config => ({
    ...config,
    external: [...config.external, /^mx-api($|\/)/]
}));
